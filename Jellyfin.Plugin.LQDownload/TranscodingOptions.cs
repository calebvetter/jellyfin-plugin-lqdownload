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
		public int TargetBitrate { get; set; }

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
	}
}
