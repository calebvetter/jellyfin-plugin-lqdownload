using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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
  /// <param name="mediaEncoder">The media encoder for handling video encoding.</param>
  public class TranscodingHandler(ILibraryManager libraryManager, ILogger<TranscodingHandler> logger, IMediaEncoder mediaEncoder) : IDisposable {
	private readonly ILibraryManager _libraryManager = libraryManager;
	private readonly ILogger<TranscodingHandler> _logger = logger;
	private readonly IMediaEncoder _mediaEncoder = mediaEncoder;
	private readonly ConcurrentDictionary<Guid, Video> _pendingItems = new();
	private readonly CancellationTokenSource _cancellationTokenSource = new();
	private bool _disposed;

	/// <summary>
	/// Finalizes an instance of the <see cref="TranscodingHandler"/> class.
	/// Finalizer for <see cref="TranscodingHandler"/> to clean up resources if Dispose is not called.
	/// </summary>
	~TranscodingHandler() {
	  Dispose(false);
	}

	/// <summary>
	/// Initializes the transcoding handler by subscribing to the library item added event.
	/// </summary>
	public void Initialize() {
	  _logger.LogInformation("LQDOWNLOAD: Initializing TranscodingHandler");
	  _libraryManager.ItemAdded += OnItemAdded;

	  // Start background task to process pending items
	  Task.Run(() => ProcessPendingItemsAsync(_cancellationTokenSource.Token));
	}

	/// <summary>
	/// Handles the item added event and initiates transcoding if necessary.
	/// </summary>
	/// <param name="sender">The event sender.</param>
	/// <param name="e">The event arguments containing the item information.</param>
	private void OnItemAdded(object? sender, ItemChangeEventArgs e) {
	  _logger.LogInformation("LQDOWNLOAD: Item added to library");

	  if (e.Item is Video video) {
		_logger.LogInformation("LQDOWNLOAD: Item is a video");

		if (video.Width > 0 && video.Height > 0) {
		  // Metadata is available; process immediately
		  _logger.LogInformation("LQDOWNLOAD: Metadata available for {VideoName}. Proceeding to transcode.", video.Name);
		  Task.Run(() => TryTranscode(video));
		}
		else {
		  // Metadata not ready; add to pending list
		  _pendingItems.TryAdd(video.Id, video);
		  _logger.LogInformation("LQDOWNLOAD: Metadata not yet available for {VideoName}. Added to pending list.", video.Name);
		}
	  }
	}

	private async Task ProcessPendingItemsAsync(CancellationToken cancellationToken) {
	  while (!cancellationToken.IsCancellationRequested) {
		try {
		  foreach (var itemId in _pendingItems.Keys) {
			if (_pendingItems.TryGetValue(itemId, out var video)) {
			  // Check if metadata is now available
			  if (video.Width > 0 && video.Height > 0) {
				_logger.LogInformation("LQDOWNLOAD: Metadata now available for {VideoName}. Proceeding to transcode.", video.Name);

				// Remove from pending list
				_pendingItems.TryRemove(itemId, out _);

				// Transcode
				await TryTranscode(video).ConfigureAwait(false);
			  }
			}
		  }

		  // Wait before checking again
		  await Task.Delay(5000, cancellationToken).ConfigureAwait(false); // Check every 5 seconds
		}
		catch (TaskCanceledException) {
		  // Graceful shutdown
		  break;
		}
		catch (Exception ex) {
		  _logger.LogError(ex, "LQDOWNLOAD: Error while processing pending items.");
		}
	  }
	}

	private async Task TryTranscode(Video video) {
	  var config = Plugin.Instance?.Configuration;
	  if (config == null) {
		_logger.LogError("LQDOWNLOAD: Plugin configuration is null. Unable to transcode video.");
		return;
	  }

	  if (NeedsTranscoding(video, config.Resolution, config.MaxBitrate)) {
		_logger.LogInformation("Video needs transcoding: {VideoName}", video.Name);
		await TranscodeVideo(video, config.Resolution, config.MaxBitrate).ConfigureAwait(false);
	  }
	  else {
		_logger.LogInformation("LQDOWNLOAD: Video {VideoName} does not need transcoding.", video.Name);
	  }
	}

	/// <summary>
	/// Determines if the video needs transcoding based on the configured resolution and bitrate.
	/// </summary>
	/// <param name="video">The video item to be checked.</param>
	/// <param name="configResolution">The target resolution from the configuration.</param>
	/// <param name="configMaxBitrate">The target max bitrate from the configuration.</param>
	/// <returns>True if the video needs transcoding, otherwise false.</returns>
	private static bool NeedsTranscoding(Video video, ResolutionOptions configResolution, int configMaxBitrate) {
	  // Determine resolution values based on the enum
	  var (targetWidth, targetHeight) = configResolution switch {
		ResolutionOptions.Resolution1080p => (1920, 1080),
		ResolutionOptions.Resolution720p => (1280, 720),
		ResolutionOptions.Resolution480p => (854, 480),
		_ => (1920, 1080)
	  };

	  // Check if the video's resolution or bitrate is greater than configured limits
	  bool isHigherResolution = video.Width > targetWidth || video.Height > targetHeight;
	  var videoStream = video.GetDefaultVideoStream();
	  bool isHigherBitrate = videoStream != null && videoStream.BitRate.HasValue && videoStream.BitRate.Value > configMaxBitrate;

	  // If resolution is higher, always transcode. If bitrate is higher, transcode to match the lower bitrate.
	  return isHigherResolution || isHigherBitrate;
	}

	/// <summary>
	/// Transcodes the video to match the target resolution and bitrate.
	/// </summary>
	/// <param name="video">The video item to be transcoded.</param>
	/// <param name="targetResolution">The target resolution for the transcoded video.</param>
	/// <param name="maxBitrate">The target maximum bitrate for the transcoded video.</param>
	private async Task TranscodeVideo(Video video, ResolutionOptions targetResolution, int maxBitrate) {
	  var transcodingOptions = new TranscodingOptions {
		MaxHeight = targetResolution switch {
		  ResolutionOptions.Resolution1080p => 1080,
		  ResolutionOptions.Resolution720p => 720,
		  ResolutionOptions.Resolution480p => 480,
		  _ => 1080
		},
		MaxWidth = targetResolution switch {
		  ResolutionOptions.Resolution1080p => 1920,
		  ResolutionOptions.Resolution720p => 1280,
		  ResolutionOptions.Resolution480p => 854,
		  _ => 1920
		},
		MaxBitrate = maxBitrate,
		AudioCodec = "aac",
		VideoCodec = "h264"
	  };
	  var (targetWidth, targetHeight) = targetResolution switch {
		ResolutionOptions.Resolution1080p => (1920, 1080),
		ResolutionOptions.Resolution720p => (1280, 720),
		ResolutionOptions.Resolution480p => (854, 480),
		_ => (1920, 1080)
	  };

	  // Log the transcoding initiation
	  _logger.LogInformation("Starting transcoding for video: {VideoName}", video.Name);

	  try {
		// Assume the first media source is the one we need
		var mediaSources = video.GetMediaSources(true);
		var mediaSource = mediaSources.Count > 0 ? mediaSources[0] : null;
		if (mediaSource == null) {
		  _logger.LogError("No media source found for video: {VideoName}", video.Name);
		  return;
		}

		// Get input arguments for encoding using IMediaEncoder methods
		string inputArgument = _mediaEncoder.GetInputArgument(mediaSource.Path, mediaSource);
		string outputFilePath = GetTranscodedFilePath(video);
		string encoderPath = _mediaEncoder.EncoderPath;

		// Create and start an external process to run ffmpeg with the appropriate arguments
		var processStartInfo = new ProcessStartInfo {
		  FileName = encoderPath,
		  Arguments = $"-i {inputArgument} -vf scale={targetWidth}:{targetHeight} -b:v {maxBitrate}k -c:v {transcodingOptions.VideoCodec} -c:a {transcodingOptions.AudioCodec} {outputFilePath}",
		  RedirectStandardOutput = true,
		  RedirectStandardError = true,
		  UseShellExecute = false,
		  CreateNoWindow = true
		};

		using var process = new Process { StartInfo = processStartInfo };
		process.Start();
		string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
		string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
		await process.WaitForExitAsync().ConfigureAwait(false);

		if (process.ExitCode == 0) {
		  _logger.LogInformation("Transcoding completed successfully for video: {VideoName}", video.Name);
		}
		else {
		  _logger.LogError("Transcoding failed for video: {VideoName}. Error: {Error}", video.Name, error);
		}
	  }
	  catch (Exception ex) {
		_logger.LogError(ex, "Failed to transcode video: {VideoName}. Error: {Error}", video.Name, ex.Message);
		throw;
	  }
	}

	/// <summary>
	/// Generates the file path for the transcoded video output.
	/// </summary>
	/// <param name="video">The video item for which the file path is generated.</param>
	/// <returns>The generated file path for the transcoded output.</returns>
	private static string GetTranscodedFilePath(Video video) {
	  // Generate a new file path for the transcoded file
	  var directory = System.IO.Path.GetDirectoryName(video.Path) ?? System.IO.Path.GetTempPath();
	  var fileName = $"{System.IO.Path.GetFileNameWithoutExtension(video.Path)}_transcoded.mp4";
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
		// Dispose managed resources
		_libraryManager.ItemAdded -= OnItemAdded;
		_cancellationTokenSource.Cancel();
		_cancellationTokenSource.Dispose();
	  }

	  // Dispose unmanaged resources here if any

	  _disposed = true;
	}
  }
}
