using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload {
	/// <summary>
	/// Handles transcoding when new media is added.
	/// </summary>
	public class TranscodingHandler : IDisposable {
		private readonly ILibraryManager _libraryManager;
		private readonly IServerConfigurationManager _serverConfigurationManager;
		private readonly EncodingHelper _encodingHelper;
		private readonly ILogger<TranscodingHandler> _logger;
		private readonly ConcurrentDictionary<Guid, TranscodeQueueItem> _transcodeQueue = new();
		private readonly ConcurrentDictionary<string, string> _itemsToBeLinked = [];
		private readonly CancellationTokenSource _cancellationTokenSource = new();
		private readonly SemaphoreSlim _transcodeSemaphore = new(1, 1);
		private readonly JsonSerializerOptions _jsonOptions = new() {
			WriteIndented = true
		};

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
		/// <param name="encodingHelper">An instance of the encoding helper to assist with media encoding operations.</param>
		/// <param name="logger">The logger instance for logging events, errors, and other messages.</param>
		public TranscodingHandler(
				ILibraryManager libraryManager,
				IServerConfigurationManager serverConfigurationManager,
				EncodingHelper encodingHelper,
				ILogger<TranscodingHandler> logger) {
			_libraryManager = libraryManager;
			_serverConfigurationManager = serverConfigurationManager;
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
			_libraryManager.ItemUpdated += OnItemUpdated;

			// Start background task to process pending items
			Task.Run(() => ProcessQueueAsync(_cancellationTokenSource.Token));
		}

		/// <summary>
		/// Handles the item added event and initiates transcoding if necessary.
		/// </summary>
		private void OnItemAdded(object? sender, ItemChangeEventArgs e) {
			_logger.LogInformation("Item added {Id} {Name}", e.Item.Id, e.Item.Name);

			if (_itemsToBeLinked.TryGetValue(e.Item.Path, out var originalItemPath)) {
				// This is a video that was transcoded by this plugin
				var originalItem = _libraryManager.FindByPath(originalItemPath, false);
				if (originalItem != null) {
					TryLinkItem(e.Item, originalItem);
				}

				return;
			}

			// Don't do anything if not configured for encode on import
			if (Plugin.Instance?.Configuration.EncodeOnImport != true) {
				return;
			}

			if (e.Item is Video video) {
				AddToQueue(video);
			}
		}

		private void OnItemUpdated(object? sender, ItemChangeEventArgs e) {
			_logger.LogInformation("Item updated {Id} {Name}", e.Item.Id, e.Item.Name);

			if (_itemsToBeLinked.TryGetValue(e.Item.Path, out var originalItemPath)) {
				// This is a video that was transcoded by this plugin
				var originalItem = _libraryManager.FindByPath(originalItemPath, false);
				if (originalItem != null) {
					TryLinkItem(e.Item, originalItem);
				}

				return;
			}
		}

		private void OnItemRemoved(object? sender, ItemChangeEventArgs e) {
			_logger.LogInformation("Item removed {Id} {Name}", e.Item.Id, e.Item.Name);

			if (e.Item is Video video && _transcodeQueue.TryGetValue(video.Id, out _)) {
				if (video.Id == _currentVideoId) {
					_logger.LogInformation("Stopping current transcoding for video: {VideoName}", video.Name);
					StopCurrentTranscode();
				}

				_transcodeQueue.TryRemove(video.Id, out _);
				_logger.LogInformation("Video {VideoName} removed from the transcode queue.", video.Name);
			}
		}

		private async void TryLinkItem(BaseItem newItem, BaseItem originalItem) {
			_itemsToBeLinked.Remove(newItem.Path, out _);

			if (originalItem is not Video originalVideo) {
				return;
			}

			if (newItem.OwnerId == Guid.Empty) {
				newItem.OwnerId = originalItem.Id;
				await newItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, _cancellationTokenSource.Token).ConfigureAwait(false);
				originalVideo.LocalAlternateVersions = [.. originalVideo.LocalAlternateVersions ?? [], newItem.Path];
				await originalVideo.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, _cancellationTokenSource.Token).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Adds video to transcode queue.
		/// </summary>
		/// <param name="video">The video to add to the queue.</param>
		/// <returns>Successfully added to queue.</returns>
		public bool AddToQueue(Video video) {
			if (video == null) {
				return false;
			}

			if (_transcodeQueue.ContainsKey(video.Id)) {
				_logger.LogInformation("Video {VideoName} is already in the queue.", video.Name);
				return false;
			}

			// Add to queue
			_transcodeQueue.TryAdd(video.Id, new() {
				Video = video,
				Progress = 0
			});
			_logger.LogInformation("Video {VideoName} added to the transcoding queue.", video.Name);

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
					_logger.LogError(ex, "Error while processing added items.");
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
			_logger.LogInformation("Refreshed metadata for video {VideoName} (ID: {VideoId}).", refreshedVideo.Name, videoId);
			return updatedItem;
		}

		private void LogVideoDetails(BaseItem video) {
			if (video == null) {
				_logger.LogWarning("Video object is null.");
				return;
			}

			var videoType = video.GetType();
			var fields = videoType.GetProperties();

			var videoDetails = new Dictionary<string, object>();

			foreach (var field in fields) {
				try {
					var value = field.GetValue(video);
					videoDetails[field.Name] = value ?? "null";
				}
				catch (Exception ex) {
					_logger.LogError(ex, "Error while getting field value for {FieldName}", field.Name);
				}
			}

			try {
				string json = JsonSerializer.Serialize(videoDetails, _jsonOptions);
				_logger.LogInformation("Logging all fields and values for video object:\n{VideoDetails}", json);
			}
			catch (Exception ex) {
				_logger.LogError(ex, "Error while serializing video details to JSON");
			}
		}

		/// <summary>
		/// Determines if a video requires transcoding based on resolution, bitrate, and other criteria.
		/// </summary>
		/// <param name="video">
		/// The <see cref="Video"/> object to evaluate for transcoding requirements.
		/// </param>
		/// <param name="checkAltVersions">
		/// A flag indicating whether to check alternate versions of the video.
		/// If <c>true</c>, the method will inspect alternate versions to determine if they meet the transcoding requirements.
		/// Defaults to <c>true</c>.
		/// </param>
		/// <returns>
		/// A <see cref="TranscodingOptions"/> object containing the target transcoding settings if transcoding is needed, or <c>null</c> if no transcoding is necessary.
		/// </returns>
		/// <remarks>
		/// This method checks if the video's resolution and bitrate exceed the plugin's configured limits.
		/// It also verifies if a transcoded version already exists or if the video is already in the transcoding queue.
		/// </remarks>
		public TranscodingOptions? NeedsTranscoding(Video video, bool checkAltVersions = true) {
			if (Plugin.Instance?.Configuration is not PluginConfiguration config) {
				_logger.LogWarning("Plugin configuration not found.");
				return null;
			}

			if (video == null) {
				_logger.LogWarning("Video is null.");
				return null;
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
			int targetBitrate = videoBitrate > 0 ? Math.Min(videoBitrate, config.TargetBitrate) : config.TargetBitrate;
			bool isHigherBitrate = videoBitrate > config.MaxBitrate;

			_logger.LogInformation("{VideoName} has a resolution of {Width}x{Height} and birtate of {Bitrate}kbps", video.Name, video.Width, video.Height, videoBitrate);
			_logger.LogInformation("Config target resolution is {Width}x{Height} and birtate {Bitrate}kbps", targetWidth, targetHeight, config.MaxBitrate);

			if (!isHigherResolution && !isHigherBitrate) {
				// No transcoding necessary
				return null;
			}

			// Check if transcoded version already exists, and that none are already in the queue
			if (checkAltVersions && video.LocalAlternateVersions.Length > 0) {
				var itemIds = video.GetLocalAlternateVersionIds()?.ToArray();
				if (itemIds != null && itemIds.Length > 0) {
					var versions = _libraryManager.GetItemList(new InternalItemsQuery() {
						ItemIds = itemIds
					});

					foreach (var version in versions) {
						if (version is Video videoVersion
								&& (NeedsTranscoding(videoVersion, false) == null
									|| _transcodeQueue.ContainsKey(videoVersion.Id))) {
							return null;
						}
					}
				}
			}

			// Create transcode options
			var transcodingOptions = new TranscodingOptions {
				MaxWidth = targetWidth,
				MaxHeight = targetHeight,
				TargetBitrate = targetBitrate,
				AudioCodec = "aac",
				VideoCodec = "h264",
				Container = "mp4"
			};

			// One last check to see if output file already exists (and not picked up by Jellyfin yet)
			var outputPath = GetTranscodedFilePath(video, transcodingOptions);
			if (File.Exists(outputPath)) {
				_logger.LogInformation("Video {Name} file already exists at {Path}", video.Name, outputPath);
				return null;
			}

			return transcodingOptions;
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

			if (NeedsTranscoding(video) is not TranscodingOptions options) {
				_logger.LogInformation("Video {VideoName} does not need transcoding.", video.Name);
				return;
			}

			// Wait for the semaphore to process the video
			await _transcodeSemaphore.WaitAsync().ConfigureAwait(false);
			try {
				// Update the current video ID and status
				_currentVideoId = video.Id;

				// Mark as in progress (progress > 0)
				SetQueueItemProgress(video.Id, 0.01);

				await TranscodeVideo(video, options).ConfigureAwait(false);
			}
			finally {
				_transcodeQueue.TryRemove(video.Id, out _);
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
			var mediaSources = video.GetMediaSources(true);
			var mediaSource = mediaSources.FirstOrDefault();
			if (mediaSource == null) {
				_logger.LogError("No media source found for video: {VideoName}", video.Name);
				return;
			}

			var encodingJobInfo = new EncodingJobInfo(TranscodingJobType.Progressive) {
				BaseRequest = new BaseEncodingJobOptions {
					Context = MediaBrowser.Model.Dlna.EncodingContext.Static,
				},
				VideoStream = video.GetDefaultVideoStream(),
				MediaSource = mediaSource,
				IsVideoRequest = true,
				OutputVideoCodec = "h264"
			};
			var encodingOptions = _serverConfigurationManager.GetEncodingOptions();
			var hwaccelArgs = _encodingHelper.GetInputVideoHwaccelArgs(encodingJobInfo, encodingOptions);
			var threadCount = encodingOptions.EncodingThreadCount < 0 ? 0 : encodingOptions.EncodingThreadCount;
			var outputPath = GetTranscodedFilePath(video, transcodingOptions);

			// Check if tmp file exists (delete)
			if (File.Exists(outputPath)) {
				File.Delete(outputPath);
			}

			// Check if final file exists (abort)
			string finalOutputPath = outputPath.Replace(".tmp", ".mp4", StringComparison.OrdinalIgnoreCase);
			if (File.Exists(finalOutputPath)) {
				_logger.LogInformation("Final file already exists: {Path}. Aborting transcoding.", finalOutputPath);
				return;
			}

			// TODO: REMOVE -t ARG AFTER TESTING
			var arguments = $"-i \"{mediaSource.Path}\" -t 10 {hwaccelArgs} -vf scale={transcodingOptions.MaxWidth}:{transcodingOptions.MaxHeight} " +
											$"-b:v {transcodingOptions.TargetBitrate}k -c:v {transcodingOptions.VideoCodec} -c:a {transcodingOptions.AudioCodec} " +
											$"-threads {threadCount} -f mp4 \"{outputPath}\"";

			_logger.LogInformation("Running ffmpeg with arguments: {Arguments}", arguments);

			var processStartInfo = new ProcessStartInfo {
				FileName = "ffmpeg",
				Arguments = arguments,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using var process = new Process {
				StartInfo = processStartInfo
			};

			_currentTranscodingProcess = process;

			process.ErrorDataReceived += ProcessFfMpegOutput;

			try {
				_logger.LogInformation("Starting transcoding for video: {VideoName}", video.Name);
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				await process.WaitForExitAsync().ConfigureAwait(false);

				if (process.ExitCode != 0) {
					throw new InvalidOperationException($"ffmpeg exit code {process.ExitCode}");
				}

				// Rename tmp file to mp4
				File.Move(outputPath, finalOutputPath);

				_logger.LogInformation("Transcoding completed for video: {Id} {VideoName}", video.Id, video.Name);

				// Add video to be linked after ingested (movies are automatic but shows need manual linking)
				_itemsToBeLinked.TryAdd(finalOutputPath, video.Path);

				// Scan library to pick up new file
				_libraryManager.QueueLibraryScan();
			}
			catch (Exception ex) {
				_logger.LogError(ex, "Error while transcoding video {VideoName}: {Error}", video.Name, ex.Message);

				// Remove partially transcoded file
				if (Path.Exists(outputPath)) {
					File.Delete(outputPath);
				}
			}
			finally {
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
			var durationMatch = Regex.Match(args.Data, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
			if (durationMatch.Success) {
				_currentVideoDuration = new TimeSpan(
					0,
					int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture),
					int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture),
					(int)Math.Floor(double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture)),
					(int)((double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture) % 1) * 1000));
			}

			// Parse the progress by matching the "time" field in the ffmpeg output
			var timeMatch = Regex.Match(args.Data, @"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})");
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

				_logger.LogInformation("Current progress: {Progress:0.##}%", _currentProgress);
			}
		}

		private static string GetTranscodedFilePath(Video video, TranscodingOptions transcodingOptions) {
			var directory = Path.GetDirectoryName(video.Path) ?? Path.GetTempPath();
			var originalFileName = Path.GetFileNameWithoutExtension(video.Path);

			// Check if the filename contains " - " and truncate it
			var dashIndex = originalFileName.LastIndexOf(" - ", StringComparison.Ordinal);
			if (dashIndex >= 0) {
				originalFileName = originalFileName[..dashIndex];
			}

			// Construct the tags based on transcoding options
			var sortTag = "[zzz]";
			var resolutionTag = $"[{transcodingOptions.MaxHeight}p]";
			var bitrateTag = $"[{transcodingOptions.TargetBitrate}kbps]";
			var tags = $"{sortTag}{resolutionTag}{bitrateTag}";

			// Add the " - " and tags
			var newFileName = $"{originalFileName} - {tags}";

			// Construct the full output path, with a temporary extension initially
			return Path.Combine(directory, $"{newFileName}.tmp");
		}

		private void StopCurrentTranscode() {
			if (_currentVideoId == Guid.Empty || _currentTranscodingProcess == null) {
				return; // No video currently being transcoded
			}

			try {
				if (!_currentTranscodingProcess.HasExited) {
					_logger.LogInformation("Stopping FFmpeg process for video ID: {VideoId}", _currentVideoId);
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
	}
}
