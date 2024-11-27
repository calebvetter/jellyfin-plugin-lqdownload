using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload.Api {
	/// <summary>
	/// Controller that provides API endpoints for retrieving the current status of active transcoding operations.
	/// </summary>
	[ApiController]
	[Route("LQDownload/TranscodingStatus")]
	public class TranscodingStatusController : ControllerBase {
		private readonly ILogger<TranscodingStatusController> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="TranscodingStatusController"/> class.
		/// </summary>
		/// <param name="logger">Logger for the controller.</param>
		public TranscodingStatusController(ILogger<TranscodingStatusController> logger) {
			_logger = logger;

			_logger.LogInformation("TranscodingStatusController Loaded");
		}

		/// <summary>
		/// Gets the current status of all active transcoding jobs.
		/// </summary>
		/// <returns>A collection of objects containing the transcoding status, including video ID, video name, and progress percentage.</returns>
		[HttpGet]
		public IActionResult GetTranscodingStatus() {
			if (Plugin.Instance == null) {
				return StatusCode(500, "Plugin instance not available.");
			}

			var statuses = Plugin.Instance.TranscodingStatus
					.Values
					.Select(status => new {
						VideoId = status.Video.Id,
						VideoName = status.Video.Name,
						Progress = status.Progress
					})
					.ToList();

			return Ok(statuses);
		}
	}
}
