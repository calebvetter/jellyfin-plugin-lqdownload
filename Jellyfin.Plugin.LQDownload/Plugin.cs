using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.LQDownload.Api;
using Jellyfin.Plugin.LQDownload.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable {
	private readonly TranscodingHandler _transcodingHandler;
	private readonly ConcurrentDictionary<Guid, (Video Video, double Progress)> _transcodingStatus = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="Plugin"/> class.
	/// </summary>
	/// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
	/// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
	/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
	/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
	public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, ILogger<TranscodingHandler> logger)
			: base(applicationPaths, xmlSerializer) {
		Instance = this;

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

	/// <summary>
	/// Gets the transcoding status for the plugin.
	/// </summary>
	public IReadOnlyDictionary<Guid, (Video Video, double Progress)> TranscodingStatus => _transcodingStatus;

	/// <summary>
	/// Adds or updates the transcoding status for a video.
	/// </summary>
	/// <param name="videoId">The unique identifier for the video.</param>
	/// <param name="video">The video object.</param>
	/// <param name="progress">The progress of transcoding as a percentage.</param>
	public void UpdateTranscodingStatus(Guid videoId, Video video, double progress) {
		_transcodingStatus[videoId] = (video, progress);
	}

	/// <summary>
	/// Removes the transcoding status for a video.
	/// </summary>
	/// <param name="videoId">The unique identifier for the video to be removed.</param>
	public void RemoveTranscodingStatus(Guid videoId) {
		_transcodingStatus.TryRemove(videoId, out _);
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
		serviceCollection.AddSingleton<TranscodingStatusController>();
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
