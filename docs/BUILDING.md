# Building from source

You need the **.NET 10 SDK**.

```bash
cd Jellyfin.Plugin.YtDlp
dotnet build -c Release
```

The plugin is `Jellyfin.Plugin.YtDlp/bin/Release/net10.0/Jellyfin.Plugin.YtDlp.dll`. It references `Jellyfin.Controller` and `Jellyfin.Model` 12.4.0 (from NuGet, not copied into the output) and embeds the pages in `Web/`.

To package a version:

1. Set `AssemblyVersion` and `FileVersion` in `Jellyfin.Plugin.YtDlp.csproj` (for example `1.6.0.0`).
2. Make a folder `MediaDownloader_<version>` with the built dll and a `meta.json` (copy the previous release's and change `version`, `timestamp` and `changelog`; keep the `guid` `16118734-a017-4a44-8210-7baef431cea4`).
3. Zip the two files: `media-downloader_<version>.zip`.

## Layout

| Path | What it is |
| --- | --- |
| `Plugin.cs` | The plugin, its dashboard page |
| `ServiceRegistrator.cs` | Services and the side-menu link's startup filter |
| `DownloadQueue.cs` | The queue that runs yt-dlp, names files for Jellyfin, writes .nfo, scans |
| `UrlChecker.cs` | The link check |
| `YtDlpBinary.cs`, `DenoBinary.cs`, `UpdateYtDlpTask.cs` | Fetching, checking (SHA-256) and updating yt-dlp and deno |
| `StorageService.cs` | Drives, folder sizes, files with watched state, delete |
| `FolderService.cs` | Users' folders: make, rename, move, delete; moving files |
| `MenuLink.cs` | Adds the menu link to `web/config.json` as it is sent |
| `Api/YtDlpController.cs` | The administrator's API (`/YtDlp/...`) |
| `Api/MediaDownloaderController.cs`, `Api/UserAccessInfo.cs` | The users' page and API (`/MediaDownloader/...`), and who may use which folders |
| `Configuration/PluginConfiguration.cs` | Settings |
| `Web/ytdlp.html`, `Web/ytdlp.js` | The dashboard page |
| `Web/user.html`, `Web/user.js` | The users' page |
