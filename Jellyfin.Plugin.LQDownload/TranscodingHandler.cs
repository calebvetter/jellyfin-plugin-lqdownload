using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
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
			logger.LogInformation("LQDOWNLOAD: Initializing TranscodingHandler");
			libraryManager.ItemAdded += OnItemAdded;

			// Start background task to process pending items
			Task.Run(() => ProcessPendingItemsAsync(_cancellationTokenSource.Token));
		}

		/// <summary>
		/// Handles the item added event and initiates transcoding if necessary.
		/// </summary>
		private void OnItemAdded(object? sender, ItemChangeEventArgs e) {
			logger.LogInformation("LQDOWNLOAD: Item added to library");

			if (e.Item is Video video) {
				logger.LogInformation("LQDOWNLOAD: Item is a video");

				if (video.Width > 0 && video.Height > 0) {
					logger.LogInformation("LQDOWNLOAD: Metadata available for {VideoName}. Proceeding to transcode.", video.Name);
					Task.Run(() => TryTranscode(video));
				}
				else {
					_pendingItems.TryAdd(video.Id, video);
					logger.LogInformation("LQDOWNLOAD: Metadata not yet available for {VideoName}. Added to pending list.", video.Name);
				}
			}
		}

		private async Task ProcessPendingItemsAsync(CancellationToken cancellationToken) {
			while (!cancellationToken.IsCancellationRequested) {
				try {
					foreach (var itemId in _pendingItems.Keys) {
						if (_pendingItems.TryGetValue(itemId, out var video)) {
							if (video.Width > 0 && video.Height > 0) {
								logger.LogInformation("LQDOWNLOAD: Metadata now available for {VideoName}. Proceeding to transcode.", video.Name);
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
					logger.LogError(ex, "LQDOWNLOAD: Error while processing pending items.");
				}
			}
		}

		private async Task TryTranscode(Video video) {
			await _transcodeSemaphore.WaitAsync().ConfigureAwait(false);
			try {
				var config = Plugin.Instance?.Configuration;
				if (config == null) {
					logger.LogError("LQDOWNLOAD: Plugin configuration is null. Unable to transcode video.");
					return;
				}

				if (NeedsTranscoding(video, config.Resolution, config.MaxBitrate) is TranscodingOptions transcodingOptions) {
					logger.LogInformation("LQDOWNLOAD: Video needs transcoding: {VideoName}", video.Name);
					await TranscodeVideo(video, transcodingOptions).ConfigureAwait(false);
				}
				else {
					logger.LogInformation("LQDOWNLOAD: Video {VideoName} does not need transcoding.", video.Name);
				}
			}
			finally {
				_transcodeSemaphore.Release();
			}
		}

		private TranscodingOptions? NeedsTranscoding(Video video, ResolutionOptions configResolution, int configMaxBitrate) {
			var (targetWidth, targetHeight) = configResolution switch {
				ResolutionOptions.Resolution1080p => (1920, 1080),
				ResolutionOptions.Resolution720p => (1280, 720),
				ResolutionOptions.Resolution480p => (854, 480),
				_ => (1920, 1080)
			};

			logger.LogInformation("Video has a resolution of {Width}x{Height}", video.Width, video.Height);

			bool isHigherResolution = video.Width > targetWidth || video.Height > targetHeight;
			var videoStream = video.GetDefaultVideoStream();
			logger.LogInformation("Video has a bitrate of {Bitrate}", videoStream.BitRate);
			bool isHigherBitrate = videoStream != null && videoStream.BitRate.HasValue && videoStream.BitRate.Value > configMaxBitrate * 1024;

			if (isHigherResolution || isHigherBitrate) {
				return new TranscodingOptions {
					MaxWidth = targetWidth,
					MaxHeight = targetHeight,
					MaxBitrate = configMaxBitrate,
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
				logger.LogError("LQDOWNLOAD: No media source found for video: {VideoName}", video.Name);
				return;
			}

			var outputPath = GetTranscodedFilePath(video);
			var arguments = $"-i \"{mediaSource.Path}\" -vf scale={transcodingOptions.MaxWidth}:{transcodingOptions.MaxHeight} " +
											$"-b:v {transcodingOptions.MaxBitrate}k -c:v {transcodingOptions.VideoCodec} -c:a {transcodingOptions.AudioCodec} " +
											$"\"{outputPath}\"";

			logger.LogInformation("LQDOWNLOAD: Running ffmpeg with arguments: {Arguments}", arguments);

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

			process.OutputDataReceived += (sender, args) => logger.LogInformation("{Data}", args.Data);
			process.ErrorDataReceived += (sender, args) => logger.LogError("{Data}", args.Data);

			try {
				logger.LogInformation("LQDOWNLOAD: Starting transcoding for video: {VideoName}", video.Name);
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				await process.WaitForExitAsync().ConfigureAwait(false);

				if (process.ExitCode == 0) {
					logger.LogInformation("LQDOWNLOAD: Transcoding completed for video: {VideoName}", video.Name);
				}
				else {
					logger.LogError("LQDOWNLOAD: Transcoding failed for video: {VideoName} with exit code {ExitCode}", video.Name, process.ExitCode);
				}
			}
			catch (Exception ex) {
				logger.LogError(ex, "LQDOWNLOAD: Error while transcoding video: {VideoName}", video.Name);
			}
		}

		private static string GetTranscodedFilePath(Video video) {
			var directory = System.IO.Path.GetDirectoryName(video.Path) ?? System.IO.Path.GetTempPath();
			var fileName = $"{System.IO.Path.GetFileNameWithoutExtension(video.Path)}_lqdownload.mp4";
			return System.IO.Path.Combine(directory, fileName);
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
