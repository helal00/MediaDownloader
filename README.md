# Media Downloader for Jellyfin

A Jellyfin plugin that downloads videos and audio from YouTube and about 1,800 other sites (everything [yt-dlp](https://github.com/yt-dlp/yt-dlp) supports) straight into your Jellyfin libraries, named the way Jellyfin expects, with poster, description and subtitles.

- **Download**: paste a link; it is checked first (supported site, single video or playlist, title and length). Save it as a film, a series episode, music (audio only) or a plain video, into any library folder.
- **Downloads**: progress, speed and time left; cancel; "Open in Jellyfin to play" when finished.
- **Storage**: free space, folder sizes, every file biggest first with who watched it; filters; delete files through Jellyfin.
- **People who may download**: users who do not manage the server get their own page at `<server>/MediaDownloader`, limited to the folders you give them. They see only their own downloads and files, can organise them in folders (make, rename, move, delete) and move files between them.
- **Menu link**: a "Media Downloader" link in Jellyfin's side menu for everyone. Nothing in Jellyfin's own files changes, so it stays after Jellyfin updates.
- **No setup of tools**: the plugin downloads yt-dlp and deno itself (SHA-256 checked) and keeps them up to date every week. Jellyfin's own ffmpeg is used.

Current version: **1.6.1.0**, for **Jellyfin 12.x** (target ABI 12.0.0.0). Tested on Jellyfin 12.4.0 on Windows 11; the code also supports Linux (x64, arm64, armv7) and macOS.

## Documents

| Document | What it covers |
| --- | --- |
| [docs/INSTALL.md](docs/INSTALL.md) | Adding the plugin to Jellyfin on Windows, Linux, Docker and macOS; updating; removing |
| [docs/USING.md](docs/USING.md) | Using it: the administrator's page, settings, the page for other users, folders, storage |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Common problems and fixes |
| [docs/BUILDING.md](docs/BUILDING.md) | Building the plugin from this source |
| [CHANGELOG.md](CHANGELOG.md) | Changes per version |

## Quick install

1. Download `media-downloader_1.6.1.0.zip` from this repository's [Releases](../../releases).
2. Stop Jellyfin.
3. Unpack the zip into a new folder `MediaDownloader_1.6.1.0` inside Jellyfin's `plugins` folder (on Windows `C:\ProgramData\Jellyfin\Server\plugins`).
4. Start Jellyfin. Open **Dashboard › Plugins**: "Media Downloader" is listed as Active, and **Media Downloader** appears in the dashboard menu.

Full steps for every system: [docs/INSTALL.md](docs/INSTALL.md).

## What it does not do

- It does not get around sites' sign-ins, paywalls or DRM; what yt-dlp cannot download, this cannot either.
- You are responsible for what you download and for following the sites' terms and copyright law where you live.
- It does not back up your libraries; deleting through it deletes the files for good.
