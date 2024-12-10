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
/// Specifies the available video codec options for transcoding.
/// </summary>
public enum VideoCodecOptions {
	/// <summary>
	/// H.264 codec (also known as AVC), offering broad compatibility and high quality.
	/// Recommended for devices with limited codec support or older hardware.
	/// </summary>
	H264,

	/// <summary>
	/// H.265 codec (also known as HEVC), providing better compression efficiency and smaller file sizes.
	/// Ideal for modern devices and environments where storage or bandwidth optimization is crucial.
	/// </summary>
	H265
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration {
	/// <summary>
	/// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
	/// </summary>
	public PluginConfiguration() {
		// Setup config
		IsIndexPatched = false;
		IsInDocker = false;
		BasePath = string.Empty;
		IndexPath = string.Empty;

		// Default options
		EncodeOnImport = false;
		VideoCodec = VideoCodecOptions.H265;
		Resolution = ResolutionOptions.Resolution1080p;
		TargetBitrate = 2500;
		MaxBitrate = 3500;
	}

	/// <summary>
	/// Gets or sets a value indicating whether whether or not the index.html file has been patched
	/// to add the client-side script tag.
	/// </summary>
	public bool IsIndexPatched { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether whether or not Jellyfin is running in a Docker container.
	/// </summary>
	public bool IsInDocker { get; set; }

	/// <summary>
	/// Gets or sets the base path for URLs.
	/// </summary>
	public string BasePath { get; set; }

	/// <summary>
	/// Gets or sets the file system path for the index.html file.
	/// </summary>
	public string IndexPath { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether new media is
	/// automatically transcoded when imported.
	/// </summary>
	public bool EncodeOnImport { get; set; }

	/// <summary>
	/// Gets or sets the video codec used for transcoding.
	/// </summary>
	/// <remarks>
	/// Available options include H.264 (AVC) and H.265 (HEVC).
	/// H.264 provides broad compatibility, while H.265 offers better compression
	/// for smaller file sizes at similar quality.
	/// </remarks>
	public VideoCodecOptions VideoCodec { get; set; }

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
