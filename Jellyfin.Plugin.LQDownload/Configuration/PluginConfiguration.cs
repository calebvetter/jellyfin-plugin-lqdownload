using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LQDownload.Configuration;

/// <summary>
/// The configuration options.
/// </summary>
public enum ResolutionOptions {
  /// <summary>
  /// Resolution1080p resolution.
  /// </summary>
  Resolution1080p,

  /// <summary>
  /// Resolution720p resolution.
  /// </summary>
  Resolution720p,

  /// <summary>
  /// Resolution480p resolution.
  /// </summary>
  Resolution480p
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration {
  /// <summary>
  /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
  /// </summary>
  public PluginConfiguration() {
	// set default options here
	Resolution = ResolutionOptions.Resolution1080p;
	MaxBitrate = 4000;
  }

  /// <summary>
  /// Gets or sets the resolution option.
  /// </summary>
  public ResolutionOptions Resolution { get; set; }

  /// <summary>
  /// Gets or sets the maximum bitrate.
  /// </summary>
  public int MaxBitrate { get; set; }
}
