using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LQDownload.Api;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable {
	private readonly IApplicationPaths _applicationPaths;
	private readonly ILibraryManager _libraryManager;
	private readonly IServerConfigurationManager _serverConfigurationManager;
	private readonly ILogger<TranscodingHandler> _logger;
	private readonly TranscodingHandler _transcodingHandler;

	/// <summary>
	/// Initializes a new instance of the <see cref="Plugin"/> class.
	/// </summary>
	/// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface, providing application directory paths.</param>
	/// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface for handling XML serialization.</param>
	/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface to manage Jellyfin library items.</param>
	/// <param name="serverConfigurationManager">The server configuration manager to retrieve transcoding-related settings.</param>
	/// <param name="mediaEncoder">The media encoder instance for managing video and audio encoding.</param>
	/// <param name="subtitleEncoder">The subtitle encoder instance for handling subtitle processing and encoding.</param>
	/// <param name="configuration">The application configuration instance to access general Jellyfin settings.</param>
	/// <param name="configurationManager">The configuration manager for handling plugin-specific settings and operations.</param>
	/// <param name="logger">Instance of the <see cref="ILogger"/> interface for logging events, warnings, and errors.</param>
	public Plugin(
			IApplicationPaths applicationPaths,
			IXmlSerializer xmlSerializer,
			ILibraryManager libraryManager,
			IServerConfigurationManager serverConfigurationManager,
			IMediaEncoder mediaEncoder,
			ISubtitleEncoder subtitleEncoder,
			IConfiguration configuration,
			MediaBrowser.Common.Configuration.IConfigurationManager configurationManager,
			ILogger<TranscodingHandler> logger)
			: base(applicationPaths, xmlSerializer) {
		// Set the singleton instance for the plugin
		Instance = this;

		_applicationPaths = applicationPaths;
		_libraryManager = libraryManager;
		_serverConfigurationManager = serverConfigurationManager;

		// Store the logger instance
		_logger = logger;

		// Initialize the TranscodingHandler with all required dependencies
		_transcodingHandler = new TranscodingHandler(
				libraryManager,
				serverConfigurationManager,
				new EncodingHelper(applicationPaths, mediaEncoder, subtitleEncoder, configuration, configurationManager),
				logger);

		_logger.LogInformation("LQDOWNLOAD LOADING...");

		// Add frontend script
		InjectWebJs();
	}

	/// <inheritdoc />
	public override string Name => "LQ Download";

	/// <inheritdoc />
	public override Guid Id => Guid.Parse("8cdb7e71-66fc-42e1-b821-c37cab2451b6");

	/// <summary>
	/// Gets the current plugin instance.
	/// </summary>
	public static Plugin? Instance { get; private set; }

	/// <summary>
	/// Gets the transcoding status for the plugin.
	/// </summary>
	public IReadOnlyDictionary<Guid, TranscodeQueueItem> TranscodeQueue => _transcodingHandler.TranscodeQueue;

	/// <summary>
	/// Injects frontend javascript in web UI.
	/// </summary>
	private void InjectWebJs() {
		_logger.LogInformation("InjectWebJs");
		if (string.IsNullOrWhiteSpace(_applicationPaths.WebPath)) {
			_logger.LogInformation("No web path");
			return;
		}

		var indexFile = Path.Combine(_applicationPaths.WebPath, "index.html");
		if (!File.Exists(indexFile)) {
			_logger.LogInformation("No index.html");
			return;
		}

		string indexContents = File.ReadAllText(indexFile);

		string basePath = string.Empty;

		// Get base path from network config
		try {
			var networkConfig = _serverConfigurationManager.GetConfiguration("network");
			var configType = networkConfig.GetType();
			var basePathField = configType.GetProperty("BaseUrl");
			var confBasePath = basePathField?.GetValue(networkConfig)?.ToString()?.Trim('/');

			if (!string.IsNullOrEmpty(confBasePath)) {
				basePath = $"/{confBasePath}";
			}
		}
		catch (Exception e) {
			_logger.LogError("Unable to get base path from config, using '/': {Error}", e);
		}

		// Don't run if script already exists
		string scriptReplace = "<script plugin=\"LQDownload\".*?></script>";
		string scriptElement = string.Format(CultureInfo.InvariantCulture, "<script plugin=\"LQDownload\" version=\"0.0.2\" src=\"{0}/LQDownload/ClientScript\"></script>", basePath);

		if (!indexContents.Contains(scriptElement, StringComparison.Ordinal)) {
			_logger.LogInformation("Attempting to inject script code in {IndexFile}", indexFile);

			// Replace old scripts
			indexContents = Regex.Replace(indexContents, scriptReplace, string.Empty);

			// Insert script last in body
			int bodyClosing = indexContents.LastIndexOf("</body>", StringComparison.Ordinal);
			if (bodyClosing != -1) {
				indexContents = indexContents.Insert(bodyClosing, scriptElement);

				try {
					File.WriteAllText(indexFile, indexContents);
					_logger.LogInformation("Finished injecting script code in {IndexFile}", indexFile);
				}
				catch (Exception e) {
					_logger.LogError("Encountered exception while writing to {IndexFile}: {Error}", indexFile, e);
				}
			}
			else {
				_logger.LogInformation("Could not find closing body tag in {IndexFile}", indexFile);
			}
		}
		else {
			_logger.LogInformation("Found client script injected in {IndexFile}", indexFile);
		}
	}

	/// <summary>
	/// Determines whether a video requires transcoding.
	/// </summary>
	/// <param name="videoId">
	/// The <see cref="Guid"/> object to evaluate for transcoding requirements.
	/// </param>
	/// <returns>
	/// <c>true</c> if transcoding is required; otherwise, <c>false</c>.
	/// </returns>
	/// <remarks>
	/// This method delegates the evaluation to the <see cref="TranscodingHandler.NeedsTranscoding"/> method
	/// and returns <c>true</c> if the result is not <c>null</c>.
	/// </remarks>
	public bool IsTranscodingNeeded(Guid videoId) {
		// Validate and get video
		var video = GetVideo(videoId);
		if (video == null) {
			return false;
		}

		// Check if transcoding is needed
		return _transcodingHandler.NeedsTranscoding(video) != null;
	}

	/// <summary>
	/// Transcodes a video by ID.
	/// </summary>
	/// <param name="videoId">ID of the video.</param>
	/// <returns>Whether the task was started.</returns>
	public bool TranscodeVideo(Guid videoId) {
		// Validate and get video
		var video = GetVideo(videoId);
		if (video == null) {
			return false;
		}

		// Add to transcode queue
		return _transcodingHandler.AddToQueue(video);
	}

	/// <summary>
	/// Retrieves an item by its ID and checks if it is a video.
	/// </summary>
	/// <param name="videoId">The ID of the item to retrieve.</param>
	/// <returns>
	/// The item as a <see cref="Video"/> object if it is a video; otherwise, <c>null</c>.
	/// </returns>
	private Video? GetVideo(Guid videoId) {
		var item = _libraryManager.GetItemById(videoId);
		return item is Video video ? video : null;
	}

	/// <inheritdoc />
	public IEnumerable<PluginPageInfo> GetPages() {
		return new[] {
			new PluginPageInfo {
				Name = Name,
				EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
			}
		};
	}

	/// <summary>
	/// Register services in the dependency injection container.
	/// </summary>
	/// <param name="serviceCollection">The service collection.</param>
	public static void Load(IServiceCollection serviceCollection) {
		serviceCollection.AddSingleton<TranscodingHandler>();
		serviceCollection.AddSingleton<ClientScriptController>();
	}

	/// <summary>
	/// Releases the resources used by the plugin.
	/// </summary>
	/// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
	protected virtual void Dispose(bool disposing) {
		if (disposing) {
			// Dispose managed resources
			_transcodingHandler?.Dispose();
		}

		// Dispose unmanaged resources here if any
	}

	/// <summary>
	/// Releases all resources used by the plugin.
	/// </summary>
	public void Dispose() {
		Dispose(true);
		GC.SuppressFinalize(this);
	}
}
