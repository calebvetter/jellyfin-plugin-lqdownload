using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LQDownload {
	/// <summary>
	/// Represents an item in the transcoding queue, containing the video, progress, and transcoding options.
	/// </summary>
	public class TranscodeQueueItem {
		/// <summary>
		/// Gets or sets the video to be transcoded.
		/// </summary>
		public required Video Video { get; set; }

		/// <summary>
		/// Gets or sets the progress of the transcoding operation, represented as a percentage (0 to 100).
		/// </summary>
		public double Progress { get; set; }

		/// <summary>
		/// Enables deconstruction of the TranscodeQueueItem into its properties.
		/// </summary>
		/// <param name="video">The video to be transcoded.</param>
		/// <param name="progress">The progress of the transcoding operation.</param>
		public void Deconstruct(out Video video, out double progress) {
			video = Video;
			progress = Progress;
		}
	}
}
