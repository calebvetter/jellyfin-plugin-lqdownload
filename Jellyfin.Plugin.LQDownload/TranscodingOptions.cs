using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.LQDownload {
  /// <summary>
  /// Represents the transcoding options for media.
  /// </summary>
  public class TranscodingOptions {
	/// <summary>
	/// Gets or sets the maximum height for the transcoded video.
	/// </summary>
	public int MaxHeight { get; set; }

	/// <summary>
	/// Gets or sets the maximum width for the transcoded video.
	/// </summary>
	public int MaxWidth { get; set; }

	/// <summary>
	/// Gets or sets the maximum bitrate for the transcoded video.
	/// </summary>
	public int MaxBitrate { get; set; }

	/// <summary>
	/// Gets or sets the audio codec for the transcoded video.
	/// </summary>
	public string AudioCodec { get; set; } = "aac";

	/// <summary>
	/// Gets or sets the video codec for the transcoded video.
	/// </summary>
	public string VideoCodec { get; set; } = "h264";

	/// <summary>
	/// Gets or sets the container format for the transcoded video.
	/// </summary>
	public string Container { get; set; } = "mp4";

	/// <summary>
	/// Converts the transcoding options to a MediaInfo object.
	/// </summary>
	/// <returns>A MediaInfo object with the transcoding settings.</returns>
	public Dictionary<string, string> ToTranscodingParameters() {
	  var parameters = new Dictionary<string, string>
	  {
				{ "maxHeight", MaxHeight.ToString(CultureInfo.InvariantCulture) },
				{ "maxWidth", MaxHeight.ToString(CultureInfo.InvariantCulture) },
				{ "maxBitrate", MaxHeight.ToString(CultureInfo.InvariantCulture) },
				{ "audioCodec", AudioCodec },
				{ "videoCodec", VideoCodec },
				{ "container", Container }
	  };
	  return parameters;
	}
  }
}
