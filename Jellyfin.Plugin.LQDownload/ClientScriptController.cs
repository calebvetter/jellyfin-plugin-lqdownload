using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload.Api {
	/// <summary>
	/// API controller to serve the JavaScript file for the LQDownload plugin.
	/// </summary>
	[ApiController]
	[Route("LQDownload/ClientScript")]
	public partial class ClientScriptController : ControllerBase {
		private readonly ILibraryManager _libraryManager;
		private readonly ILogger<ClientScriptController> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="ClientScriptController"/> class.
		/// </summary>
		/// /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface to manage Jellyfin library items.</param>
		/// <param name="logger">The logger for logging events and errors.</param>
		public ClientScriptController(ILibraryManager libraryManager, ILogger<ClientScriptController> logger) {
			_libraryManager = libraryManager;
			_logger = logger;
		}

		/// <summary>
		/// Serves the embedded JavaScript file for the LQDownload plugin.
		/// </summary>
		/// <returns>
		/// The JavaScript file content with the content type set to <c>application/javascript</c>.
		/// If the file is not found, returns a 404 status code with an appropriate message.
		/// </returns>
		[HttpGet]
		public IActionResult ClientScript() {
			var assembly = Assembly.GetExecutingAssembly();
			var resourceName = "Jellyfin.Plugin.LQDownload.Web.LQDownload.js";

			using var stream = assembly.GetManifestResourceStream(resourceName);
			if (stream == null) {
				return NotFound("Script not found.");
			}

			using var reader = new StreamReader(stream);
			var content = reader.ReadToEnd();
			return Content(content, "application/javascript");
		}

		/// <summary>
		/// Transcodes video to LQ preset.
		/// </summary>
		/// <param name="itemId">ID of Video.</param>
		/// <returns>Success.</returns>
		[HttpGet("Transcode")]
		[Authorize]
		public IActionResult Transcode([FromQuery] string itemId) {
			if (!Guid.TryParse(itemId, out var videoId)) {
				return NotFound("Invalid guid");
			}

			var success = Plugin.Instance!.TranscodeVideo(videoId);
			return Ok(new {
				success
			});
		}

		/// <summary>
		/// Checks if transcoding is required for a specific video item and returns the target resolution.
		/// </summary>
		/// <param name="itemId">The ID of the item to check.</param>
		/// <returns>
		/// A JSON object with:
		/// <list type="bullet">
		///   <item>
		///     <description><c>configResolution</c>: The target resolution from the plugin configuration ("1080p", "720p", or "480p").</description>
		///   </item>
		///   <item>
		///     <description><c>needsTranscoding</c>: A boolean indicating whether transcoding is required for the specified item.</description>
		///   </item>
		/// </list>
		/// </returns>
		[HttpGet("TranscodeStatus")]
		public IActionResult TranscodeStatus([FromQuery] string itemId) {
			if (!Guid.TryParse(itemId, out var videoId)) {
				return NotFound("Invalid guid");
			}

			var resolution = Plugin.Instance!.Configuration.Resolution switch {
				ResolutionOptions.Resolution1080p => "1080p",
				ResolutionOptions.Resolution720p => "720p",
				ResolutionOptions.Resolution480p => "480p",
				_ => "1080p"
			};

			var (status, transcodeProgress, path, _) = Plugin.Instance!.GetTranscodeStatus(videoId);

			if (status == LQDownload.TranscodeStatus.Completed && !string.IsNullOrEmpty(path)) {
				// Extract resolution from filename tag
				var fileName = Path.GetFileNameWithoutExtension(path);
				var match = ResolutionRegex().Match(fileName);
				if (match.Success) {
					resolution = match.Groups[1].Value; // Capture the resolution (e.g., "1080p")
				}
			}

			return Ok(new {
				item = new {
					id = itemId,
					status,
					transcodeProgress,
					resolution
				}
			});
		}

		[GeneratedRegex(@"\[(\d{3,4}p)\]")]
		private static partial Regex ResolutionRegex();

		/// <summary>
		/// Handles the download request for a video file by its ID.
		/// </summary>
		/// <param name="itemId">The GUID of the video item, provided as a query parameter.</param>
		/// <returns>
		/// A file download response containing the requested video file with the ".lqdownload" extension removed,
		/// or an error response if the file does not exist or the provided GUID is invalid.
		/// </returns>
		/// <remarks>
		/// This endpoint is protected and requires authorization.
		/// The returned file will have the ".mp4" extension after removing ".lqdownload".
		/// </remarks>
		[HttpGet("Download")]
		[Authorize]
		public IActionResult Download([FromQuery] string itemId) {
			if (!Guid.TryParse(itemId, out var videoId)) {
				return NotFound("Invalid guid");
			}

			if (_libraryManager.GetItemById(videoId.ToString()) is not Video video) {
				return NotFound("Video not found");
			}

			var directory = Path.GetDirectoryName(video.Path);

			if (directory == null || !Directory.Exists(directory)) {
				return NotFound("Directory not found");
			}

			var lqDownloadFile = Directory.EnumerateFiles(directory, "*.lqdownload").FirstOrDefault();

			if (lqDownloadFile == null) {
				return NotFound("File not found");
			}

			// Modify the file name by removing the ".lqdownload" extension
			var fileName = Path.GetFileName(lqDownloadFile)
				.Replace(
					".lqdownload",
					string.Empty,
					StringComparison.OrdinalIgnoreCase);

			return PhysicalFile(lqDownloadFile, "video/mp4", fileName);
		}
	}
}
