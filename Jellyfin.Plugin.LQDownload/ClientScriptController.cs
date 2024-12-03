using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Controller.Entities;
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
		public IActionResult Transcode([FromQuery] string itemId) {
			// TODO: verify user is authenticated
			var success = Plugin.Instance!.TranscodeVideo(itemId);
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
			_logger.LogInformation("Get transcode status for {Id}", itemId);
			var configResolution = Plugin.Instance!.Configuration.Resolution switch {
				ResolutionOptions.Resolution1080p => "1080p",
				ResolutionOptions.Resolution720p => "720p",
				ResolutionOptions.Resolution480p => "480p",
				_ => "1080p"
			};
			var maxBitrate = Plugin.Instance!.Configuration.MaxBitrate;

			var needsTranscoding = false;
			double transcodeProgress = 0;

			var video = Plugin.Instance.GetVideo(itemId);
			if (video != null) {
				needsTranscoding = true;

				if (video.LocalAlternateVersions.Length > 0) {
					foreach (var version in video.LocalAlternateVersions) {
						_logger.LogInformation("version {Version}", version);

						// Get the filename only
						var fileName = Path.GetFileName(version);

						_logger.LogInformation("filename {Filename}", fileName);

						// Check if the file contains the resolution tag
						if (fileName.Contains(configResolution, StringComparison.OrdinalIgnoreCase)) {
							_logger.LogInformation("has resolution tag");

							// Check if it contains a bitrate tag and extract the number
							var match = Regex.Match(fileName, @"\[(\d+)kbps\]", RegexOptions.IgnoreCase);
							if (match.Success) {
								_logger.LogInformation("kbps regex match success");
								if (int.TryParse(match.Groups[1].Value, out var bitrate)) {
									_logger.LogInformation("filename bitrate {Bitrate}", bitrate);
									_logger.LogInformation("max bitrate {MaxBitrate}", maxBitrate);
									if (bitrate <= maxBitrate) {
										needsTranscoding = false;
										break; // Found a valid version, exit the loop
									}
								}
							}
						}
					}
				}

				if (needsTranscoding) {
					// TODO: see logs as to why queued transcodes show needsTranscoding = true
					foreach (var status in Plugin.Instance.TranscodingStatus) {
						_logger.LogInformation("Transcode status: {Key}, {VideoName}, {Progress}", status.Key, status.Value.Video.Name, status.Value.Progress);
					}

					// Check if transcoding job is currently running
					var matchingStatus = Plugin.Instance.TranscodingStatus.FirstOrDefault(kvp => kvp.Value.Video.Id == video.Id);
					if (!matchingStatus.Equals(default(KeyValuePair<Guid, (Video Video, double Progress)>))) {
						needsTranscoding = false;
						var transcodingVideo = matchingStatus.Value.Video;
						transcodeProgress = matchingStatus.Value.Progress;
						Console.WriteLine($"Found transcoding status: Video={transcodingVideo.Name}, Progress={transcodeProgress}%");
					}
				}
			}

			return Ok(new {
				configResolution,
				needsTranscoding,
				transcodeProgress
			});
		}
	}
}
