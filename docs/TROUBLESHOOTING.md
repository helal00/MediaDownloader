# Troubleshooting

## Jellyfin does not start after installing (Windows)

The plugin folder probably kept permissions from your user profile that Jellyfin's service account cannot read (Jellyfin's log shows `UnauthorizedAccessException` on `meta.json`). Reset them, as administrator:

```powershell
icacls 'C:\ProgramData\Jellyfin\Server\plugins\MediaDownloader_1.6.0.0' /reset /T
Start-Service JellyfinServer
```

## The plugin shows "Not supported" or "Malfunctioned"

- Your Jellyfin is older than 12.x. This plugin is built for Jellyfin 12.
- Two versions are installed: keep only the newest `MediaDownloader_*` folder.
- Jellyfin's log (Dashboard › Logs) says why it could not load.

## A download fails with 403 or "Sign in to confirm"

- **Settings › Update yt-dlp and deno now**: sites change often.
- For YouTube, try another **YouTube player types** value (`default,web_safari` or `default,mweb`).
- Make sure **Use deno** is on.
- Sites that need an account, age checks or a subscription may not work.

## "Jellyfin's service account could not ..." when downloading, deleting or moving

Give the service account **Modify** permission on the library folder (see [INSTALL.md](INSTALL.md#5-let-jellyfin-write-into-your-libraries)), and close any program that has the files open.

## The download finished but is not in Jellyfin

- **Scan the libraries after each download** must be on, or start a scan yourself (Dashboard › Libraries › Scan all libraries).
- The folder must belong to a library of the right type (films in a Movies library, episodes in a Shows library).

## A user cannot see the page or gets "may not use the downloader"

- Add them under **Settings › People who may download** with at least one folder inside a library folder, and save.
- Give them access to that library under Dashboard › Users › the user › Library access.

## The menu link does not show

- Check **Settings › Show a link to the downloader page in Jellyfin's side menu** is ticked and saved.
- Reload Jellyfin's page with **Ctrl+Shift+R**; on phones and TVs close the app and open it again.
