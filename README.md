# LQ Download

This Jellyfin plugin aims to provide a quick solution to generating and downloading lower quality transcodes. Jellyfin provides a convenient way to download the original file, but that can sometimes be a huge file. Jellyfin also has a way to stream at a lower quality, but that's no good for offline viewing, such as on a flight.

## Features

- Option for automatic encoding upon import
- Ability to transcode individual titles from the UI
- H.264 (AVC) and H.265 (HEVC) output support
- Resolution and bitrate settings
- Transcode and download API endpoints protected by authorization

## Installation

### Add repository

1. In the Admin Dashboard, go to Plugins > Catelog
2. Click the gear icon to show Repositories
3. Click the plus icon and add `https://raw.githubusercontent.com/calebvetter/jellyfin-plugin-lqdownload/main/manifest.json` as the URL and whatever you want as the Name

### Install and configure the plugin

1. In the Admin Dashboard, go to Plugins > Catelog
2. Search for the "LQ Download" plugin (or find in the General section) and install
3. Restart the Server
4. Go to the Admin Dashboard > My Plugins > LQ Download
5. If there's a message about additional setup, follow instructions to complete
6. Adjust settings as desired and enjoy your downloads!

## Using the Plugin

To start a video encoding go to the movie or episode (only works on individual episodes, not series/season batch) and click the more options button (the three dots). You should see a new option to create a file based on your resolution setting.

The status is shown on the movie/episode page and you can get multiple transcodes queued.

When a transcode is available, click the more options button and you'll see a new Download button with the resolution it's transcoded in.
