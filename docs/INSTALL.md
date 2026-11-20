# Installing Media Downloader

Media Downloader is installed by hand: you put the plugin's folder into Jellyfin's `plugins` folder and restart Jellyfin. (This repository is private, so Jellyfin cannot install it from a plugin repository link.)

## What you need

- Jellyfin Server **12.x** (the plugin is built for target ABI 12.0.0.0; it does not load on 10.x).
- Internet access from the server, so the plugin can fetch yt-dlp and deno from their GitHub releases on first use and update them every week.
- Nothing else: Jellyfin's own ffmpeg is used.

## 1. Get the plugin

Download `media-downloader_<version>.zip` from the repository's **Releases** page. It holds two files:

- `Jellyfin.Plugin.YtDlp.dll`: the plugin
- `meta.json`: its name, version and description for Jellyfin

## 2. Find Jellyfin's plugins folder

| System | Plugins folder |
| --- | --- |
| Windows (installer) | `C:\ProgramData\Jellyfin\Server\plugins` |
| Linux (Debian/Ubuntu package) | `/var/lib/jellyfin/plugins` |
| Docker (official image) | `/config/plugins` inside the container, i.e. the `plugins` folder in the volume mounted at `/config` |
| macOS | `~/.local/share/jellyfin/plugins` |

If yours is elsewhere: Dashboard › **Paths** shows the data folder; `plugins` is inside it.

## 3. Install

Stop Jellyfin first, then make a folder named `MediaDownloader_<version>` (for example `MediaDownloader_1.6.1.0`) in the plugins folder and unpack the zip into it.

### Windows

In PowerShell **as administrator** (change the version and the zip's path):

```powershell
Stop-Service JellyfinServer
$dest = 'C:\ProgramData\Jellyfin\Server\plugins\MediaDownloader_1.6.1.0'
Expand-Archive "$env:USERPROFILE\Downloads\media-downloader_1.6.1.0.zip" -DestinationPath $dest
icacls $dest /reset /T
Start-Service JellyfinServer
```

`icacls /reset` gives the folder the plugins folder's normal permissions. Without it, a folder moved from your user profile can keep permissions that Jellyfin's service account cannot read, and Jellyfin then fails to start.

If Jellyfin runs as a tray app instead of a service, quit it from the tray instead of `Stop-Service`, and start it again afterwards.

### Linux (package install)

```bash
sudo systemctl stop jellyfin
sudo mkdir -p /var/lib/jellyfin/plugins/MediaDownloader_1.6.1.0
sudo unzip media-downloader_1.6.1.0.zip -d /var/lib/jellyfin/plugins/MediaDownloader_1.6.1.0
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/MediaDownloader_1.6.1.0
sudo systemctl start jellyfin
```

### Docker

```bash
docker stop jellyfin
mkdir -p /path/to/config/plugins/MediaDownloader_1.6.1.0
unzip media-downloader_1.6.1.0.zip -d /path/to/config/plugins/MediaDownloader_1.6.1.0
docker start jellyfin
```

Make sure the files belong to the user the container runs as (the `PUID`/`PGID` or `--user` you gave it).

### macOS

Quit Jellyfin, then:

```bash
mkdir -p ~/.local/share/jellyfin/plugins/MediaDownloader_1.6.1.0
unzip ~/Downloads/media-downloader_1.6.1.0.zip -d ~/.local/share/jellyfin/plugins/MediaDownloader_1.6.1.0
```

Start Jellyfin again.

## 4. Check it

- Dashboard › **Plugins**: "Media Downloader" with the version, status **Active**.
- The dashboard's menu has **Media Downloader**.
- `http://<server>:8096/MediaDownloader` opens the page for users.

If the plugin shows as "Not supported" or "Malfunctioned", see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

## 5. Let Jellyfin write into your libraries

Jellyfin's service account must be allowed to **create, change and delete** files in the library folders you download into (and to delete, if you use Storage or let users delete):

- Windows: the service runs as `NT AUTHORITY\NetworkService` by default. Give it **Modify** on each library folder (folder › Properties › Security › Edit › Add `NETWORK SERVICE` › Modify), or in PowerShell as administrator:

  ```powershell
  icacls 'C:\MOVIES' /grant 'NETWORK SERVICE:(OI)(CI)M'
  ```

- Linux: the `jellyfin` user (package) or the container's user (Docker) needs write permission on the library folders.

The plugin's Download page says so if it cannot write somewhere.

## Updating

Stop Jellyfin, add the new version's folder (for example `MediaDownloader_1.7.0.0`), **delete the old version's folder**, then start Jellyfin. Settings are kept: they are stored separately in `plugins/configurations/Jellyfin.Plugin.YtDlp.xml`. Downloaded yt-dlp and deno are kept in `plugins/Jellyfin.Plugin.YtDlp/`.

## Removing

Stop Jellyfin and delete:

- `plugins/MediaDownloader_<version>/` (the plugin)
- optionally `plugins/Jellyfin.Plugin.YtDlp/` (its yt-dlp and deno copies)
- optionally `plugins/configurations/Jellyfin.Plugin.YtDlp.xml` (its settings)

Then start Jellyfin. Your downloaded files stay where they are. The side-menu link disappears with the plugin.
