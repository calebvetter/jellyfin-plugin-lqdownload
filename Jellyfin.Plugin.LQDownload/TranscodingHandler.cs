using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload {
	/// <summary>
	/// Represents the current status of a video in the transcoding process.
	/// </summary>
	public enum TranscodeStatus {
		/// <summary>
		/// Indicates that transcoding is not required for the video.
		/// </summary>
		NotNeeded,

		/// <summary>
		/// Indicates that the video can be transcoded if requested,
		/// but it has not been queued or started yet.
		/// </summary>
		CanTranscode,

		/// <summary>
		/// Indicates that the video has been queued for transcoding,
		/// but the process has not started yet.
		/// </summary>
		Queued,

		/// <summary>
		/// Indicates that the transcoding process is currently in progress for the video.
		/// </summary>
		Transcoding,

		/// <summary>
		/// Indicates that the transcoding process has been completed,
		/// and the transcoded file is available.
		/// </summary>
		Completed
	}

	/// <summary>
	/// Handles transcoding when new media is added.
	/// </summary>
	public partial class TranscodingHandler : IDisposable {
		private readonly ILibraryManager _libraryManager;
		private readonly IServerConfigurationManager _serverConfigurationManager;
		private readonly IMediaEncoder _mediaEncoder;
		private readonly EncodingHelper _encodingHelper;
		private readonly ILogger<TranscodingHandler> _logger;
		private readonly ConcurrentDictionary<Guid, TranscodeQueueItem> _transcodeQueue = new();
		private readonly CancellationTokenSource _cancellationTokenSource = new();
		private readonly SemaphoreSlim _transcodeSemaphore = new(1, 1);

		private Guid _currentVideoId = Guid.Empty;
		private TimeSpan? _currentVideoDuration;
		private Process? _currentTranscodingProcess;
		private double _currentProgress;

		private bool _disposed;

		/// <summary>
		/// Initializes a new instance of the <see cref="TranscodingHandler"/> class.
		/// </summary>
		/// <param name="libraryManager">The library manager to manage library items in the Jellyfin library.</param>
		/// <param name="serverConfigurationManager">The server configuration manager to retrieve transcoding-related settings.</param>
		/// <param name="mediaEncoder">An instance of the media encoder.</param>
		/// <param name="encodingHelper">An instance of the encoding helper to assist with media encoding operations.</param>
		/// <param name="logger">The logger instance for logging events, errors, and other messages.</param>
		public TranscodingHandler(
				ILibraryManager libraryManager,
				IServerConfigurationManager serverConfigurationManager,
				IMediaEncoder mediaEncoder,
				EncodingHelper encodingHelper,
				ILogger<TranscodingHandler> logger) {
			_libraryManager = libraryManager;
			_serverConfigurationManager = serverConfigurationManager;
			_mediaEncoder = mediaEncoder;
			_encodingHelper = encodingHelper;
			_logger = logger;

			Initialize();
		}

		/// <summary>
		/// Finalizes an instance of the <see cref="TranscodingHandler"/> class.
		/// </summary>
		~TranscodingHandler() {
			Dispose(false);
		}

		/// <summary>
		/// Gets currently transcoding item with progress and all queued items.
		/// </summary>
		public IReadOnlyDictionary<Guid, TranscodeQueueItem> TranscodeQueue => _transcodeQueue;

		/// <summary>
		/// Initializes the transcoding handler by subscribing to the library item added event.
		/// </summary>
		public void Initialize() {
			_libraryManager.ItemAdded += OnItemAdded;
			_libraryManager.ItemRemoved += OnItemRemoved;

			// Start background task to process pending items
			Task.Run(() => ProcessQueueAsync(_cancellationTokenSource.Token));
		}

		/// <summary>
		/// Handles the item added event and initiates transcoding if necessary.
		/// </summary>
		private void OnItemAdded(object? sender, ItemChangeEventArgs e) {
			// Don't do anything if not configured for encode on import
			if (Plugin.Instance?.Configuration.EncodeOnImport != true) {
				return;
			}

			if (e.Item is Video video) {
				AddToQueue(video);
			}
		}

		private void OnItemRemoved(object? sender, ItemChangeEventArgs e) {
			if (e.Item is Video video) {
				// Remove from transcode queue
				if (_transcodeQueue.TryGetValue(video.Id, out _)) {
					if (video.Id == _currentVideoId) {
						_logger.LogInformation("Item removed from library. Stopping current transcoding for video: {VideoName}", video.Name);
						StopCurrentTranscode();
					}

					_transcodeQueue.TryRemove(video.Id, out _);
				}
				else {
					// TODO: remove transcoded file if exists?
				}
			}
		}

		/// <summary>
		/// Adds video to transcode queue.
		/// </summary>
		/// <param name="video">The video to add to the queue.</param>
		/// <returns>Successfully added to queue.</returns>
		public bool AddToQueue(Video video) {
			if (video == null || _transcodeQueue.ContainsKey(video.Id)) {
				return false;
			}

			// Add to queue
			_transcodeQueue.TryAdd(video.Id, new() {
				Video = video,
				Progress = 0
			});
			_logger.LogInformation("Video {VideoName} added to the transcode queue.", video.Name);

			return true;
		}

		private async Task ProcessQueueAsync(CancellationToken cancellationToken) {
			while (!cancellationToken.IsCancellationRequested) {
				try {
					foreach (var itemId in _transcodeQueue.Keys) {
						if (_transcodeQueue.TryGetValue(itemId, out var queueItem)) {
							if (RefreshQueueItem(queueItem.Video.Id) is TranscodeQueueItem item) {
								var (video, progress) = item;
								if (video.Width > 0 && video.Height > 0) {
									// Process the video (semaphore ensures only one at a time)
									await TranscodeSingleVideo(queueItem).ConfigureAwait(false);
								}
							}
						}
					}

					// Check for new items at interval
					await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
				}
				catch (TaskCanceledException) {
					break;
				}
				catch (Exception ex) {
					_logger.LogError(ex, "Error while processing transcode queue.");
				}
			}
		}

		private TranscodeQueueItem? RefreshQueueItem(Guid videoId) {
			if (!_transcodeQueue.TryGetValue(videoId, out var queueItem)) {
				_logger.LogWarning("Queue item with ID {VideoId} not found.", videoId);
				return null;
			}

			// Re-fetch the video object from the library manager
			if (_libraryManager.GetItemById(videoId) is not Video refreshedVideo) {
				_logger.LogWarning("Video with ID {VideoId} not found in the library. Removing from queue.", videoId);
				_transcodeQueue.TryRemove(videoId, out _); // Remove the item if it no longer exists
				return null;
			}

			// Update the queue item with the refreshed video
			var updatedItem = new TranscodeQueueItem {
				Video = refreshedVideo,
				Progress = queueItem.Progress
			};

			_transcodeQueue.TryUpdate(videoId, updatedItem, queueItem);
			return updatedItem;
		}

		/// <summary>
		/// Determines if a video requires transcoding based on resolution, bitrate, and other criteria.
		/// </summary>
		/// <param name="video">
		/// The <see cref="Video"/> object to evaluate for transcoding requirements.
		/// </param>
		/// <param name="skipQueueCheck">
		/// Skip checking the transcode queue. This is used by the transcode method
		/// because the video will definitely be in the queue then.
		/// </param>
		/// <returns>
		/// A <see cref="TranscodingOptions"/> object containing the target transcoding settings if transcoding is needed, or <c>null</c> if no transcoding is necessary.
		/// </returns>
		/// <remarks>
		/// This method checks if the video's resolution and bitrate exceed the plugin's configured limits.
		/// It also verifies if a transcoded version already exists or if the video is already in the transcoding queue.
		/// </remarks>
		public (
				TranscodeStatus Status,
				double Progress,
				string? Path,
				TranscodingOptions? Options)
				GetTranscodeStatus(Video video, bool skipQueueCheck = false) {
			if (Plugin.Instance?.Configuration is not PluginConfiguration config) {
				_logger.LogWarning("Plugin configuration not found.");
				return (TranscodeStatus.NotNeeded, 0, null, null);
			}

			if (video == null) {
				_logger.LogWarning("Video is null.");
				return (TranscodeStatus.NotNeeded, 0, null, null);
			}

			var directory = Path.GetDirectoryName(video.Path);
			if (directory == null || !Directory.Exists(directory)) {
				_logger.LogWarning("Video directory not found.");
				return (TranscodeStatus.NotNeeded, 0, null, null);
			}

			// Check queue
			if (!skipQueueCheck && _transcodeQueue.TryGetValue(video.Id, out var videoStatus)) {
				var status = videoStatus.Progress == 0 ? TranscodeStatus.Queued : TranscodeStatus.Transcoding;
				return (status, videoStatus.Progress, null, null);
			}

			// Look for transcoded file
			var fileBaseName = Plugin.GetTranscodedFileBaseName(video.Path);
			var lqDownloadFile = Directory.EnumerateFiles(directory, $"{fileBaseName}*.lqdownload").FirstOrDefault();
			if (lqDownloadFile != null) {
				return (TranscodeStatus.Completed, 100, lqDownloadFile, null);
			}

			// Check video all versions (local and linked) to see if there's already
			// one that satisfies resolution/bitrate requirements
			var versions = new List<Video> { video };

			// Local alternate versions
			var localAlternateVideos = video.GetLocalAlternateVersionIds()?
				.SelectMany(id => _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = [id] }))
				.OfType<Video>() ?? [];
			versions.AddRange(localAlternateVideos);

			// Linked alternate versions
			versions.AddRange(video.GetLinkedAlternateVersions());

			// Perform check
			foreach (var version in versions) {
				if (version.Width > 0 && version.Height > 0 && !VideoNeedsTranscoding(version)) {
					// Video meets requirements, transcoding not needed
					return (TranscodeStatus.NotNeeded, 0, null, null);
				}
			}

			// Create transcode options
			// to the target, to minimize processing required.
			var (targetWidth, targetHeight) = config.Resolution switch {
				ResolutionOptions.Resolution1080p => (1920, 1080),
				ResolutionOptions.Resolution720p => (1280, 720),
				ResolutionOptions.Resolution480p => (854, 480),
				_ => (1920, 1080)
			};
			var transcodingOptions = new TranscodingOptions {
				MaxWidth = targetWidth,
				MaxHeight = targetHeight,
				TargetBitrate = config.TargetBitrate,
				VideoCodec = config.VideoCodec,
			};

			return (TranscodeStatus.CanTranscode, 0, null, transcodingOptions);
		}

		/// <summary>
		/// Check if video needs transcoding.
		/// </summary>
		/// <param name="video">The video to check.</param>
		/// <returns>Whether or not the video needs transcoding.</returns>
		private bool VideoNeedsTranscoding(Video video) {
			if (Plugin.Instance?.Configuration is not PluginConfiguration config) {
				return false;
			}

			var (targetWidth, targetHeight) = config.Resolution switch {
				ResolutionOptions.Resolution1080p => (1920, 1080),
				ResolutionOptions.Resolution720p => (1280, 720),
				ResolutionOptions.Resolution480p => (854, 480),
				_ => (1920, 1080)
			};

			bool isHigherResolution = video.Width > targetWidth || video.Height > targetHeight;
			var videoStream = video.GetDefaultVideoStream();
			int videoBitrate = (videoStream != null && videoStream.BitRate.HasValue ? videoStream.BitRate.Value : 0) / 1000;
			bool isHigherBitrate = videoBitrate > config.MaxBitrate;

			return isHigherResolution || isHigherBitrate;
		}

		/// <summary>
		/// Attempts to transcode the specified video. If another transcoding operation
		/// is already running, this method queues the video for later processing.
		/// Prevents duplicate transcoding tasks for the same video.
		/// </summary>
		/// <param name="queueItem">The queued video to be transcoded.</param>
		/// <returns>A task that represents the asynchronous operation.</returns>
		/// <remarks>
		/// This method ensures that only one transcoding operation runs at a time
		/// using a semaphore. Videos that do not require transcoding or are already
		/// queued will be skipped.
		/// </remarks>
		private async Task TranscodeSingleVideo(TranscodeQueueItem queueItem) {
			var (video, _) = queueItem;
			if (video == null) {
				return;
			}

			// Wait for the semaphore to process the video
			await _transcodeSemaphore.WaitAsync().ConfigureAwait(false);
			try {
				if (GetTranscodeStatus(video, true).Options is not TranscodingOptions options) {
					throw new InvalidOperationException($"Video {video.Name} does not need transcoding.");
				}

				// Update the current video ID and status
				_currentVideoId = video.Id;

				// Mark as in progress (progress > 0)
				SetQueueItemProgress(video.Id, 0.01);

				await TranscodeVideo(video, options).ConfigureAwait(false);
			}
			catch (Exception ex) {
				_logger.LogError("{Error}", ex.Message);
			}
			finally {
				_transcodeQueue.TryRemove(video.Id, out _);
				_transcodeSemaphore.Release();
			}
		}

		private void SetQueueItemProgress(Guid videoId, double progress) {
			if (_transcodeQueue.TryGetValue(videoId, out var queueItem)) {
				var updatedItem = new TranscodeQueueItem {
					Video = queueItem.Video,
					Progress = progress
				};

				_transcodeQueue.TryUpdate(videoId, updatedItem, queueItem);
			}
		}

		private async Task TranscodeVideo(Video video, TranscodingOptions transcodingOptions) {
			_logger.LogInformation("Starting transcode for video: {Id} {VideoName}", video.Id, video.Name);

			var outputPath = GetTranscodedFilePath(video, transcodingOptions);

			// Check if tmp file exists (abort)
			if (File.Exists(outputPath)) {
				_logger.LogWarning("Transcoded file already exists: {Path}. Aborting transcode.", outputPath);
				return;
			}

			var mediaSources = video.GetMediaSources(true);
			var mediaSource = mediaSources.FirstOrDefault();
			if (mediaSource == null) {
				_logger.LogError("No media source found for video: {Id} {VideoName}", video.Id, video.Name);
				return;
			}

			var videoStream = video.GetDefaultVideoStream();

			// Codec
			var videoCodec = transcodingOptions.VideoCodec switch {
				VideoCodecOptions.H265 => "hevc",
				VideoCodecOptions.H264 => "h264",
				_ => throw new InvalidOperationException("Unsupported codec option selected.")
			};

			// Jellyfin options
			var encodingJobInfo = new EncodingJobInfo(TranscodingJobType.Progressive) {
				BaseRequest = new BaseEncodingJobOptions {
					Context = MediaBrowser.Model.Dlna.EncodingContext.Static,
				},
				VideoStream = videoStream,
				MediaSource = mediaSource,
				IsVideoRequest = true,
				OutputVideoCodec = videoCodec
			};
			var encodingOptions = _serverConfigurationManager.GetEncodingOptions();
			var hwaccelArgs = _encodingHelper.GetInputVideoHwaccelArgs(encodingJobInfo, encodingOptions);
			var threadCount = encodingOptions.EncodingThreadCount < 0 ? 0 : encodingOptions.EncodingThreadCount;

			// Process external subtitle files
			var subtitleArgs = new StringBuilder();
			var mapArgs = new StringBuilder();
			var externalSubtitleIndex = 1; // External file inputs start at index 1
			if (video.SubtitleFiles != null) {
				foreach (var subtitleFile in video.SubtitleFiles) {
					if (!string.IsNullOrEmpty(subtitleFile)) {
						subtitleArgs.Append(CultureInfo.InvariantCulture, $"-i \"{subtitleFile}\" ");
						mapArgs.Append(CultureInfo.InvariantCulture, $"-map {externalSubtitleIndex}:s ");
						externalSubtitleIndex++;
					}
				}
			}

			// Container format
			var format = transcodingOptions.Container switch {
				"mkv" => "matroska",
				_ => "mp4"
			};

			// Bitrates
			var bitrate = transcodingOptions.TargetBitrate;
			var maxrate = (int)Math.Round(bitrate * 1.2);
			var bufsize = bitrate * 2;

			var arguments = $"-i \"{mediaSource.Path}\" {subtitleArgs} {hwaccelArgs} -t 60 " +
											$"-vf \"scale={transcodingOptions.MaxWidth}:{transcodingOptions.MaxHeight}\" " +
											$"-map 0:v -map 0:a -map 0:s {mapArgs} " +
											$"-b:v {bitrate}k -maxrate {maxrate}k -bufsize {bufsize}k -c:v {videoCodec} " +
											$"-c:a {transcodingOptions.AudioCodec} -b:a 256k -c:s copy " +
											$"-threads {threadCount} -f {format} \"{outputPath}\"";

			_logger.LogInformation("Running ffmpeg with arguments: {Arguments}", arguments);

			var process = new Process {
				StartInfo = new ProcessStartInfo {
					WindowStyle = ProcessWindowStyle.Hidden,
					CreateNoWindow = true,
					UseShellExecute = false,

					// Must consume both stdout and stderr or deadlocks may occur
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					RedirectStandardInput = true,
					FileName = _mediaEncoder.EncoderPath,
					Arguments = arguments,
					ErrorDialog = false
				},
				EnableRaisingEvents = true
			};

			_currentTranscodingProcess = process;

			// All ffmpeg logs are sent to error output
			process.OutputDataReceived += ProcessFfMpegOutput;
			process.ErrorDataReceived += ProcessFfMpegOutput;

			try {
				process.Start();
				process.BeginErrorReadLine();
				await process.WaitForExitAsync().ConfigureAwait(false);

				if (process.ExitCode != 0) {
					throw new InvalidOperationException($"ffmpeg exit code {process.ExitCode}");
				}

				_logger.LogInformation("Transcode completed for video: {Id} {VideoName}", video.Id, video.Name);
			}
			catch (Exception ex) {
				_logger.LogError(ex, "Error while transcoding video {VideoName}: {Error}", video.Name, ex.Message);

				// Remove partially transcoded file
				if (Path.Exists(outputPath)) {
					File.Delete(outputPath);
				}
			}
			finally {
				process.OutputDataReceived -= ProcessFfMpegOutput;
				process.ErrorDataReceived -= ProcessFfMpegOutput;
				_currentVideoId = Guid.Empty;
				_currentVideoDuration = null;
				_currentTranscodingProcess = null;
				_currentProgress = 0;
			}
		}

		private void ProcessFfMpegOutput(object sender, DataReceivedEventArgs args) {
			if (args.Data == null) {
				return;
			}

			// If the line contains "Duration", extract the total duration of the video
			var durationMatch = DurationRegex().Match(args.Data);
			if (durationMatch.Success) {
				_currentVideoDuration = new TimeSpan(
					0,
					int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture),
					int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture),
					(int)Math.Floor(double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture)),
					(int)((double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture) % 1) * 1000));
			}

			// Parse the progress by matching the "time" field in the ffmpeg output
			var timeMatch = TimeRegex().Match(args.Data);
			if (timeMatch.Success && _currentVideoDuration.HasValue) {
				var currentTime = new TimeSpan(
						0,
						int.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture),
						int.Parse(timeMatch.Groups[2].Value, CultureInfo.InvariantCulture),
						(int)Math.Floor(double.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture)),
						(int)((double.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture) % 1) * 1000));

				_currentProgress = currentTime.TotalSeconds / _currentVideoDuration.Value.TotalSeconds * 100;

				if (_currentVideoId != Guid.Empty) {
					// Update the transcoding progress
					SetQueueItemProgress(_currentVideoId, _currentProgress);
				}
			}
		}

		private static string GetTranscodedFilePath(Video video, TranscodingOptions transcodingOptions) {
			var directory = Path.GetDirectoryName(video.Path) ?? Path.GetTempPath();
			var fileBaseName = Plugin.GetTranscodedFileBaseName(video.Path);

			// Construct the tags based on transcoding options
			var resolutionTag = $"[{transcodingOptions.MaxHeight}p]";
			var bitrateTag = $"[{transcodingOptions.TargetBitrate}kbps]";
			var tags = $"{resolutionTag}{bitrateTag}";

			// Construct filename with " - ", tags, and extensions.
			// Add proprietary extension so it doesn't get picked up by Jellyfin
			var newFileName = $"{fileBaseName} - {tags}.{transcodingOptions.Container}.lqdownload";

			// Construct the full output path
			return Path.Combine(directory, newFileName);
		}

		private void StopCurrentTranscode() {
			if (_currentVideoId == Guid.Empty || _currentTranscodingProcess == null) {
				return; // No video currently being transcoded
			}

			try {
				if (!_currentTranscodingProcess.HasExited) {
					_currentTranscodingProcess.Kill();
				}
			}
			catch (Exception ex) {
				_logger.LogError(ex, "Failed to stop transcoding process for video ID: {VideoId}", _currentVideoId);
			}
			finally {
				_currentTranscodingProcess.ErrorDataReceived -= ProcessFfMpegOutput;
				_currentVideoId = Guid.Empty;
				_currentVideoDuration = null;
				_currentTranscodingProcess = null;
				_currentProgress = 0;
			}
		}

		/// <summary>
		/// Releases all resources used by the <see cref="TranscodingHandler"/> class.
		/// </summary>
		public void Dispose() {
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Releases the unmanaged resources used by the <see cref="TranscodingHandler"/>
		/// and optionally releases the managed resources.
		/// </summary>
		/// <param name="disposing">
		/// True to release both managed and unmanaged resources; false to release only unmanaged resources.
		/// </param>
		protected virtual void Dispose(bool disposing) {
			if (_disposed) {
				return;
			}

			if (disposing) {
				_libraryManager.ItemAdded -= OnItemAdded;
				_libraryManager.ItemRemoved -= OnItemRemoved;
				_cancellationTokenSource.Cancel();
				_cancellationTokenSource.Dispose();
				_transcodeSemaphore.Dispose();

				// Dispose the current transcoding process if it's still running
				if (_currentTranscodingProcess != null) {
					try {
						if (!_currentTranscodingProcess.HasExited) {
							_currentTranscodingProcess.Kill();
						}
					}
					catch (Exception ex) {
						_logger.LogWarning(ex, "Failed to kill FFmpeg process during disposal.");
					}

					_currentTranscodingProcess.Dispose();
					_currentTranscodingProcess = null;
				}
			}

			_disposed = true;
		}

		[GeneratedRegex(@"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})")]
		private static partial Regex DurationRegex();

		[GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})")]
		private static partial Regex TimeRegex();
	}
}
