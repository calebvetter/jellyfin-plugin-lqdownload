using System;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

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
		/// Generates a short-lived token to authenticate a file download request.
		/// </summary>
		/// <param name="itemId">The GUID of the video item for which the token is being generated.</param>
		/// <returns>An HTTP response containing the download URL with the generated token.</returns>
		/// <response code="200">The token was successfully generated, and the URL is returned.</response>
		/// <response code="400">The provided itemId is invalid.</response>
		/// <response code="404">The specified video item could not be found.</response>
		[HttpGet("GetToken")]
		[Authorize]
		public IActionResult GetToken([FromQuery] string itemId) {
			if (!Guid.TryParse(itemId, out var videoId)) {
				return BadRequest("Invalid GUID");
			}

			if (_libraryManager.GetItemById(videoId.ToString()) is not Video) {
				return NotFound("Video not found");
			}

			// Generate a JWT token that expires, with video ID
			var tokenHandler = new JwtSecurityTokenHandler();
			var key = Encoding.ASCII.GetBytes(Plugin.SecretKey);
			var tokenDescriptor = new SecurityTokenDescriptor {
				Subject = new ClaimsIdentity(new[] { new Claim("itemId", itemId) }),
				Expires = DateTime.UtcNow.AddDays(1),
				Issuer = "LQDownload",
				Audience = "DownloadVideo",
				SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
			};
			var token = tokenHandler.CreateToken(tokenDescriptor);
			var tokenString = tokenHandler.WriteToken(token);

			var downloadUrl = Url.Action("Download", "ClientScript", new { token = tokenString }, Request.Scheme);
			return Ok(new { downloadUrl });
		}

		/// <summary>
		/// Handles the download request for a video file by its ID.
		/// </summary>
		/// <param name="token">The JWT which contains the ID of the video item.</param>
		/// <returns>
		/// A file download response containing the requested video file with the ".lqdownload" extension removed,
		/// or an error response if the file does not exist or the provided GUID is invalid.
		/// </returns>
		/// <remarks>
		/// This endpoint is protected and requires authorization.
		/// The returned file will have the ".mkv" extension after removing ".lqdownload".
		/// </remarks>
		[HttpGet("Download")]
		public async Task Download([FromQuery] string token) {
			var res = HttpContext.Response;

			// Validate and decode the token
			var tokenHandler = new JwtSecurityTokenHandler();
			var key = Encoding.ASCII.GetBytes(Plugin.SecretKey); // Use the dynamically generated key

			try {
				// Validate the token
				var validationParameters = new TokenValidationParameters {
					ValidateIssuerSigningKey = true,
					IssuerSigningKey = new SymmetricSecurityKey(key),
					ValidIssuer = "LQDownload",
					ValidAudience = "DownloadVideo",
					ClockSkew = TimeSpan.Zero
				};

				var validationResult = await tokenHandler.ValidateTokenAsync(token, validationParameters).ConfigureAwait(false);

				if (!validationResult.IsValid
						|| validationResult.Claims.FirstOrDefault(c => c.Key == "itemId").Value is not string itemId
						|| !Guid.TryParse(itemId, out var videoId)) {
					res.StatusCode = StatusCodes.Status400BadRequest;
					await res.WriteAsync("Invalid token payload.").ConfigureAwait(false);
					return;
				}

				// Retrieve the video item
				if (_libraryManager.GetItemById(videoId.ToString()) is not Video video) {
					res.StatusCode = StatusCodes.Status404NotFound;
					return;
				}

				var directory = Path.GetDirectoryName(video.Path);
				if (directory == null || !Directory.Exists(directory)) {
					res.StatusCode = StatusCodes.Status404NotFound;
					return;
				}

				var fileBaseName = Plugin.GetTranscodedFileBaseName(video.Path);
				var lqDownloadFile = Directory.EnumerateFiles(directory, $"{fileBaseName}*.lqdownload").FirstOrDefault();
				if (lqDownloadFile == null) {
					res.StatusCode = StatusCodes.Status404NotFound;
					return;
				}

				var fileInfo = new FileInfo(lqDownloadFile);

				// Modify the file name by removing the ".lqdownload" extension
				var fileName = Path.GetFileName(lqDownloadFile)
						.Replace(".lqdownload", string.Empty, StringComparison.OrdinalIgnoreCase);

				res.ContentType = "video/x-matroska";
				res.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
				res.Headers.ContentLength = fileInfo.Length;
				res.Headers.AcceptRanges = "bytes";

				var i = 0;
				try {
					// Open the file stream
					using var fileStream = new FileStream(lqDownloadFile, FileMode.Open, FileAccess.Read, FileShare.Read);
					const int BufferSize = 64 * 1024; // 64 KB
					byte[] buffer = new byte[BufferSize];
					int bytesRead;

					do {
						// Ensure the client is still connected
						if (HttpContext.RequestAborted.IsCancellationRequested) {
							// Gracefully exit if the client disconnects
							return;
						}

						// Read a chunk from the file
						bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, BufferSize), HttpContext.RequestAborted).ConfigureAwait(false);

						if (bytesRead > 0) {
							// Write the chunk to the response
							await res.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted).ConfigureAwait(false);
							await res.Body.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false); // Flush ensures immediate sending
						}

						i++;
					} while (bytesRead > 0); // Continue until no more data is read
				}
				catch (OperationCanceledException) {
					// Handle client disconnection gracefully
					_logger.LogInformation("Client disconnected.");
				}
				catch (Exception ex) {
					if (!res.HasStarted) {
						res.StatusCode = StatusCodes.Status500InternalServerError;
					}

					_logger.LogError("Error while streaming file: {Error}", ex.Message);
				}
			}
			catch (SecurityTokenException ex) {
				res.StatusCode = StatusCodes.Status401Unauthorized;
				await res.WriteAsync($"Invalid token: {ex.Message}").ConfigureAwait(false);
			}
			catch (Exception ex) {
				res.StatusCode = StatusCodes.Status500InternalServerError;
				await res.WriteAsync($"Unexpected error: {ex.Message}").ConfigureAwait(false);
			}
		}
	}
}
