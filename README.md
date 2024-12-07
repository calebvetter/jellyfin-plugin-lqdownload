# LQ Download

This Jellyfin plugin aims to provide a quick solution to generating and downloading lower quality transcodes. Jellyfin provides a convenient way to download the original file, but that can sometimes be a huge file. Jellyfin also has a way to stream at a lower quality, but that's no good for offline viewing, such as on a flight.

## Features

- Option for automatic encoding upon import (note: currently only works for movies, not shows)
- Ability to transcode individual titles from the UI
- H.264 (AVC) and H.265 (HEVC) output support
- Resolution and bitrate settings
- Transcode and download API endpoints protected by authorization

