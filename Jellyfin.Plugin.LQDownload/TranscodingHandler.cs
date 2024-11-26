using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload {
	/// <summary>
	/// Handles transcoding when new media is added.
	/// </summary>
	/// <remarks>
	/// Initializes a new instance of the <see cref="TranscodingHandler"/> class.
	/// </remarks>
	/// <param name="libraryManager">The library manager to manage library items.</param>
	/// <param name="logger">The logger for logging events and errors.</param>
	public class TranscodingHandler(
		ILibraryManager libraryManager,
		ILogger<TranscodingHandler> logger) : IDisposable {
		private readonly ConcurrentDictionary<Guid, Video> _pendingItems = new();
		private readonly CancellationTokenSource _cancellationTokenSource = new();
		private readonly SemaphoreSlim _transcodeSemaphore = new(1, 1);

		private readonly ConcurrentDictionary<Guid, (Video Video, double Progress)> _transcodingStatus = new();
		private Guid _currentVideoId = Guid.Empty;
		private TimeSpan? _currentVideoDuration;
		private double _currentProgress;

		private bool _disposed;

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
			libraryManager.ItemAdded += OnItemAdded;

			// Start background task to process pending items
			Task.Run(() => ProcessPendingItemsAsync(_cancellationTokenSource.Token));
		}

		/// <summary>
		/// Handles the item added event and initiates transcoding if necessary.
		/// </summary>
		private void OnItemAdded(object? sender, ItemChangeEventArgs e) {
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
					logger.LogError(ex, "Error while processing added items.");
				}
			}
		}

		private async Task TryTranscode(Video video) {
			_currentVideoId = video.Id;
			_transcodingStatus[_currentVideoId] = (video, 0);
			await _transcodeSemaphore.WaitAsync().ConfigureAwait(false);
			try {
				if (NeedsTranscoding(video) is TranscodingOptions transcodingOptions) {
					await TranscodeVideo(video, transcodingOptions).ConfigureAwait(false);
				}
				else {
					logger.LogInformation("{VideoName} does not need transcoding.", video.Name);
				}
			}
			finally {
				_transcodingStatus.TryRemove(_currentVideoId, out _);
				_transcodeSemaphore.Release();
			}
		}

		private TranscodingOptions? NeedsTranscoding(Video video) {
			if (Plugin.Instance?.Configuration is not PluginConfiguration config) {
				logger.LogWarning("Plugin configuration not found.");
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
			int videoBitrate = videoStream != null && videoStream.BitRate.HasValue ? videoStream.BitRate.Value : 0;
			int targetBitrate = videoBitrate > 0 ? Math.Min(videoBitrate, config.MaxBitrate) : config.MaxBitrate;
			bool isHigherBitrate = videoBitrate > config.MaxBitrate * 1000;

			logger.LogInformation("{VideoName} has a resolution of {Width}x{Height} and birtate of {Bitrate}kbps", video.Name, video.Width, video.Height, videoBitrate / 1000);
			logger.LogInformation("Config target resolution is {Width}x{Height} and birtate {Bitrate}kbps", targetWidth, targetHeight, config.MaxBitrate);

			if (isHigherResolution || isHigherBitrate) {
				return new TranscodingOptions {
					MaxWidth = targetWidth,
					MaxHeight = targetHeight,
					TargetBitrate = targetBitrate,
					AudioCodec = "aac",
					VideoCodec = "h264",
					Container = "mp4"
				};
			}

			return null;
		}

		private async Task TranscodeVideo(Video video, TranscodingOptions transcodingOptions) {
			var mediaSources = video.GetMediaSources(true);
			var mediaSource = mediaSources.FirstOrDefault();
			if (mediaSource == null) {
				logger.LogError("No media source found for video: {VideoName}", video.Name);
				return;
			}

			var outputPath = GetTranscodedFilePath(video, transcodingOptions);
			var arguments = $"-i \"{mediaSource.Path}\" -vf scale={transcodingOptions.MaxWidth}:{transcodingOptions.MaxHeight} " +
											$"-b:v {transcodingOptions.TargetBitrate}k -c:v {transcodingOptions.VideoCodec} -c:a {transcodingOptions.AudioCodec} " +
											$"-f mp4 \"{outputPath}\"";

			logger.LogInformation("Running ffmpeg with arguments: {Arguments}", arguments);

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
				logger.LogInformation("Starting transcoding for video: {VideoName}", video.Name);
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				await process.WaitForExitAsync().ConfigureAwait(false);

				if (process.ExitCode == 0) {
					// Rename tmp file to mp4
					string finalOutputPath = outputPath.Replace(".tmp", ".mp4", StringComparison.OrdinalIgnoreCase);
					System.IO.File.Move(outputPath, finalOutputPath);

					logger.LogInformation("Transcoding completed for video: {VideoName}", video.Name);
				}
				else {
					logger.LogError("Transcoding failed for video: {VideoName} with exit code {ExitCode}", video.Name, process.ExitCode);
				}
			}
			catch (Exception ex) {
				logger.LogError(ex, "Error while transcoding video: {VideoName}", video.Name);
			}
			finally {
				process.ErrorDataReceived -= ProcessFfMpegOutput;
				_currentVideoId = Guid.Empty;
				_currentVideoDuration = null;
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
					_transcodingStatus.TryUpdate(
							_currentVideoId,
							(_transcodingStatus[_currentVideoId].Video, _currentProgress),
							_transcodingStatus[_currentVideoId]);
				}

				logger.LogInformation("Current progress: {Progress:0.##}%", _currentProgress);
			}
		}

		private static string GetTranscodedFilePath(Video video, TranscodingOptions transcodingOptions) {
			var directory = System.IO.Path.GetDirectoryName(video.Path) ?? System.IO.Path.GetTempPath();
			var originalFileName = System.IO.Path.GetFileNameWithoutExtension(video.Path);

			// Construct the tags based on transcoding options
			var resolutionTag = $"{transcodingOptions.MaxHeight}p";
			var bitrateTag = $"{transcodingOptions.TargetBitrate}kbps";
			var tags = $"[{resolutionTag}][{bitrateTag}]";

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
			return System.IO.Path.Combine(directory, $"{newFileName}.tmp");
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
				libraryManager.ItemAdded -= OnItemAdded;
				_cancellationTokenSource.Cancel();
				_cancellationTokenSource.Dispose();
				_transcodeSemaphore.Dispose();
			}

			_disposed = true;
		}
	}
}
