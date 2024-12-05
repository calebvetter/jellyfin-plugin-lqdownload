using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload.Api {
	/// <summary>
	/// API controller to serve the JavaScript file for the LQDownload plugin.
	/// </summary>
	[ApiController]
	[Route("LQDownload/ClientScript")]
	public class ClientScriptController : ControllerBase {
		private readonly ILogger<ClientScriptController> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="ClientScriptController"/> class.
		/// </summary>
		/// <param name="logger">The logger for logging events and errors.</param>
		public ClientScriptController(ILogger<ClientScriptController> logger) {
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

			var configResolution = Plugin.Instance!.Configuration.Resolution switch {
				ResolutionOptions.Resolution1080p => "1080p",
				ResolutionOptions.Resolution720p => "720p",
				ResolutionOptions.Resolution480p => "480p",
				_ => "1080p"
			};

			bool needsTranscoding;
			var isTranscoding = false;
			double transcodeProgress = 0;

			// Check if video is transcoding or in queue
			if (Plugin.Instance.TranscodeQueue.TryGetValue(videoId, out var videoStatus)) {
				needsTranscoding = true;
				isTranscoding = true;
				transcodeProgress = videoStatus.Progress;
			}

			// Check if video needs to be transcoded
			else {
				needsTranscoding = Plugin.Instance.IsTranscodingNeeded(videoId);
			}

			return Ok(new {
				configResolution,
				item = new {
					id = itemId,
					needsTranscoding,
					isTranscoding,
					transcodeProgress
				}
			});
		}
	}
}
