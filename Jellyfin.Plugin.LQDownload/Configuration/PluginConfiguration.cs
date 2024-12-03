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
		// Default options
		EncodeOnImport = true;
		Resolution = ResolutionOptions.Resolution1080p;
		TargetBitrate = 4000;
		MaxBitrate = 6000;
	}

	/// <summary>
	/// Gets or sets a value indicating whether new media is
	/// automatically transcoded when imported.
	/// </summary>
	public bool EncodeOnImport { get; set; }

	/// <summary>
	/// Gets or sets the resolution option.
	/// </summary>
	public ResolutionOptions Resolution { get; set; }

	/// <summary>
	/// Gets or sets the target bitrate.
	/// </summary>
	public int TargetBitrate { get; set; }

	/// <summary>
	/// Gets or sets the maximum bitrate.
	/// </summary>
	public int MaxBitrate { get; set; }
}
