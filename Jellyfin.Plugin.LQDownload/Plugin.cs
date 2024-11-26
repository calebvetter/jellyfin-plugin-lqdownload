using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable {
  private readonly TranscodingHandler _transcodingHandler;

  /// <summary>
  /// Initializes a new instance of the <see cref="Plugin"/> class.
  /// </summary>
  /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
  /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
  /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
  /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
  /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
  public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, ILogger<TranscodingHandler> logger, IMediaEncoder mediaEncoder)
				  : base(applicationPaths, xmlSerializer) {
	Instance = this;

	// // Register the configuration type
	// logger.LogInformation("LQDOWNLOAD: Registering plugin configuration...");
	// configManager.RegisterConfiguration<PluginConfiguration>();

	// // Ensure configuration is initialized
	// var configKey = Id.ToString();

	// try {
	//  if (configManager.GetConfiguration(configKey) == null) {
	//      logger.LogWarning("LQDOWNLOAD: Configuration not found. Creating default configuration...");
	//      var defaultConfig = new PluginConfiguration();
	//      configManager.SaveConfiguration(configKey, defaultConfig);
	//      logger.LogInformation("LQDOWNLOAD: Default plugin configuration created.");
	//  }
	//  else {
	//      logger.LogInformation("LQDOWNLOAD: Plugin configuration already exists.");
	//  }
	// }
	// catch (Exception ex) {
	//  logger.LogError(ex, "LQDOWNLOAD: Failed to initialize plugin configuration.");
	//  throw; // Let Jellyfin handle plugin load failure
	// }

	// Initialize the TranscodingHandler
	_transcodingHandler = new TranscodingHandler(libraryManager, logger);
	_transcodingHandler.Initialize();
  }

  /// <inheritdoc />
  public override string Name => "LQ Download";

  /// <inheritdoc />
  public override Guid Id => Guid.Parse("8cdb7e71-66fc-42e1-b821-c37cab2451b6");

  /// <summary>
  /// Gets the current plugin instance.
  /// </summary>
  public static Plugin? Instance { get; private set; }

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
