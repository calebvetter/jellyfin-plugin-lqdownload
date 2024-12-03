using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
		private readonly ConcurrentDictionary<Guid, Video> _pendingItems = new();
		private readonly CancellationTokenSource _cancellationTokenSource = new();
		private readonly SemaphoreSlim _transcodeSemaphore = new(1, 1);

		private Guid _currentVideoId = Guid.Empty;
		private TimeSpan? _currentVideoDuration;
		private double _currentProgress;

		private bool _disposed;

		/// <summary>
		/// Initializes a new instance of the <see cref="TranscodingHandler"/> class.
		/// </summary>
		/// <param name="libraryManager">The library manager to manage library items in the Jellyfin library.</param>
		/// <param name="serverConfigurationManager">The server configuration manager to retrieve transcoding-related settings.</param>
		/// <param name="encodingHelper">An instance of the encoding helper to assist with media encoding operations.</param>
		/// <param name="logger">The logger instance for logging events, errors, and other messages.</param>
		public TranscodingHandler(ILibraryManager libraryManager, IServerConfigurationManager serverConfigurationManager, EncodingHelper encodingHelper, ILogger<TranscodingHandler> logger) {
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
		/// Initializes the transcoding handler by subscribing to the library item added event.
		/// </summary>
		public void Initialize() {
			_libraryManager.ItemAdded += OnItemAdded;
			// TODO: when item is removed, remove from queue

			// Start background task to process pending items
			Task.Run(() => ProcessPendingItemsAsync(_cancellationTokenSource.Token));
		}

		/// <summary>
		/// Handles the item added event and initiates transcoding if necessary.
		/// </summary>
		private void OnItemAdded(object? sender, ItemChangeEventArgs e) {
			// Don't do anything if not configured for encode on import
			if (!(Plugin.Instance?.Configuration.EncodeOnImport ?? false)) {
				return;
			}

			if (e.Item is Video video) {
				if (video.Width > 0 && video.Height > 0) {
					Task.Run(() => TryTranscode(video));
				}
				else {
					_pendingItems.TryAdd(video.Id, video);
				}
			}
		}

		private async Task ProcessPendingItemsAsync(CancellationToken cancellationToken) {
			while (!cancellationToken.IsCancellationRequested) {
				try {
					foreach (var itemId in _pendingItems.Keys) {
						if (_pendingItems.TryGetValue(itemId, out var video)) {
							if (video.Width > 0 && video.Height > 0) {
								_pendingItems.TryRemove(itemId, out _);
								await TryTranscode(video).ConfigureAwait(false);
							}
						}
					}

					await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
				}
				catch (TaskCanceledException) {
					break;
				}
				catch (Exception ex) {
					_logger.LogError(ex, "Error while processing added items.");
				}
			}
		}

		/// <summary>
		/// Attempts to transcode the specified video. If another transcoding operation
		/// is already running, this method queues the video for later processing.
		/// Prevents duplicate transcoding tasks for the same video.
		/// </summary>
		/// <param name="video">The video to be transcoded.</param>
		/// <returns>A task that represents the asynchronous operation.</returns>
		/// <remarks>
		/// This method ensures that only one transcoding operation runs at a time
		/// using a semaphore. Videos that do not require transcoding or are already
		/// queued will be skipped.
		/// </remarks>
		public async Task TryTranscode(Video video) {
			if (video == null) {
				return;
			}

			if (_pendingItems.ContainsKey(video.Id)) {
				_logger.LogInformation("Video {VideoName} is already queued for transcoding.", video.Name);
				return;
			}

			_pendingItems.TryAdd(video.Id, video);
			_logger.LogInformation("Video {VideoName} added to the transcoding queue.", video.Name);

			await _transcodeSemaphore.WaitAsync().ConfigureAwait(false);
			try {
				_pendingItems.TryRemove(video.Id, out _);
				_currentVideoId = video.Id;
				Plugin.Instance?.UpdateTranscodingStatus(_currentVideoId, video, 0);

				if (NeedsTranscoding(video) is TranscodingOptions transcodingOptions) {
					await TranscodeVideo(video, transcodingOptions).ConfigureAwait(false);
				}
				else {
					_logger.LogInformation("Video {VideoName} does not need transcoding.", video.Name);
				}
			}
			finally {
				Plugin.Instance?.RemoveTranscodingStatus(_currentVideoId);
				_transcodeSemaphore.Release();
			}
		}

		private TranscodingOptions? NeedsTranscoding(Video video) {
			if (Plugin.Instance?.Configuration is not PluginConfiguration config) {
				_logger.LogWarning("Plugin configuration not found.");
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

			if (isHigherResolution || isHigherBitrate) {
				var transcodingOptions = new TranscodingOptions {
					MaxWidth = targetWidth,
					MaxHeight = targetHeight,
					TargetBitrate = targetBitrate,
					AudioCodec = "aac",
					VideoCodec = "h264",
					Container = "mp4"
				};

				// Check if a file with the tags already exists
				var directory = Path.GetDirectoryName(video.Path) ?? Path.GetTempPath();
				var files = Directory.GetFiles(directory);
				var resolutionTag = $"[{transcodingOptions.MaxHeight}p]";
				var bitrateTag = $"[{transcodingOptions.TargetBitrate}kbps]";
				foreach (var file in files) {
					if (Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
							&& file.Contains(resolutionTag, StringComparison.OrdinalIgnoreCase)
							&& file.Contains(bitrateTag, StringComparison.OrdinalIgnoreCase)) {
						return null;
					}
				}

				return transcodingOptions;
			}

			return null;
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
			var arguments = $"-i \"{mediaSource.Path}\" {hwaccelArgs} -vf scale={transcodingOptions.MaxWidth}:{transcodingOptions.MaxHeight} " +
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

			process.ErrorDataReceived += ProcessFfMpegOutput;

			try {
				_logger.LogInformation("Starting transcoding for video: {VideoName}", video.Name);
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				await process.WaitForExitAsync().ConfigureAwait(false);

				if (process.ExitCode == 0) {
					// Rename tmp file to mp4
					string finalOutputPath = outputPath.Replace(".tmp", ".mp4", StringComparison.OrdinalIgnoreCase);
					File.Move(outputPath, finalOutputPath);

					_logger.LogInformation("Transcoding completed for video: {VideoName}", video.Name);

					// Scan library to pick up new file
					_libraryManager.QueueLibraryScan();
				}
				else {
					_logger.LogError("Transcoding failed for video: {VideoName} with exit code {ExitCode}", video.Name, process.ExitCode);
				}
			}
			catch (Exception ex) {
				_logger.LogError(ex, "Error while transcoding video: {VideoName}", video.Name);
			}
			finally {
				process.ErrorDataReceived -= ProcessFfMpegOutput;
				_currentVideoId = Guid.Empty;
				_currentVideoDuration = null;
				_currentProgress = 0;
				Plugin.Instance?.RemoveTranscodingStatus(video.Id);
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

				if (_currentVideoId != Guid.Empty && Plugin.Instance != null) {
					// Update the transcoding status via the Plugin instance
					if (Plugin.Instance.TranscodingStatus.TryGetValue(_currentVideoId, out var status)) {
						Plugin.Instance.UpdateTranscodingStatus(_currentVideoId, status.Video, _currentProgress);
					}
				}

				_logger.LogInformation("Current progress: {Progress:0.##}%", _currentProgress);
			}
		}

		private static string GetTranscodedFilePath(Video video, TranscodingOptions transcodingOptions) {
			var directory = Path.GetDirectoryName(video.Path) ?? Path.GetTempPath();
			var originalFileName = Path.GetFileNameWithoutExtension(video.Path);

			// Construct the tags based on transcoding options
			var resolutionTag = $"[{transcodingOptions.MaxHeight}p]";
			var bitrateTag = $"[{transcodingOptions.TargetBitrate}kbps]";
			var tags = $"{resolutionTag}{bitrateTag}";

			// Check if the filename already contains a dash
			string newFileName;
			if (originalFileName.Contains('-', StringComparison.Ordinal)) {
				// Add tags directly if a dash is already present
				newFileName = $"{originalFileName} {tags}";
			}
			else {
				// Add tags with a dash separator if no dash is present
				newFileName = $"{originalFileName} - {tags}";
			}

			// Construct the full output path, with a temporary extension initially
			return Path.Combine(directory, $"{newFileName}.tmp");
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
				_cancellationTokenSource.Cancel();
				_cancellationTokenSource.Dispose();
				_transcodeSemaphore.Dispose();
			}

			_disposed = true;
		}
	}
}
