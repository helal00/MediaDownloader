# Using Media Downloader

There are two pages:

- **The administrator's page**: Dashboard › **Media Downloader** (tabs Download, Downloads, Storage, Settings). Only administrators can open the dashboard.
- **The page for everyone you allow**: `http://<server>:8096/MediaDownloader` (tabs Download, My downloads, My files, My folders), also reached from the **Media Downloader** link in Jellyfin's side menu (☰). People sign in with their own Jellyfin account. Administrators can use it too, with every library folder.

## Downloading

1. Paste a link to a video, a playlist or a channel page. It is checked as you paste: a green box says the site is supported and shows the title and length; a red box explains why not and shows what working links for that site look like.
2. **Save as**:
   - **Film**: its own folder `Title (Year)` with poster and description, for a Movies library.
   - **Series episode**: `Channel / Season YEAR / dated episode`, for a Shows library.
   - **Music**: audio only (M4A) in `Artist / Album`, for a Music library.
   - **Plain video**: the file only, for a Mixed/Home videos library.
3. **Subtitles** (not for Music): choose none, one or several languages. **Choose...** opens the full list; type to find a language, or type a code the list does not have (for example `pt-BR`). When you paste a link to one video, the list first shows the languages that video has: from the uploader, and automatic ones (machine-made, often translated). A chosen language the video does not have is marked "not on this video". Your choice is remembered by the browser for the next download. A language the site refuses (for example YouTube's "429 Too Many Requests") or does not have is skipped: the video is still saved, and the download says which subtitles were saved and which were skipped.
4. Choose the folder (and optionally a new sub folder name, made if it is not there), the highest quality, and whether to take the whole playlist or channel. **Skip videos downloaded before** avoids downloading the same video twice into that folder.
5. **Download**. The Downloads tab shows progress; when Jellyfin has added the file, **Open in Jellyfin to play** appears.

Downloads run one at a time, in order. Cancelling stops the current one and removes its partial files.

## Storage (administrator) and My files (users)

- Free space of each drive with a library, and the size of each library folder (or each of the user's folders).
- Every file, biggest first, with who watched it (users see only their own watched state). Filter by Watched to the end, Started, Not watched, or Not in Jellyfin's library yet, and by name.
- Tick files and **Delete selected**: the file and its poster, description and subtitles go, and folders left empty are removed (never the library folder or the user's own folder). This cannot be undone.
- Users can also tick files and **Move selected to** another of their folders. A film saved in its own folder moves with that folder and its poster.

## My folders (users)

Click a folder, then:

- **New folder inside**: makes a folder in it.
- **Rename** or **Move it into** another folder.
- **Delete**: the folder and everything in it (only for people allowed to delete).

The user's own top folder cannot be renamed, moved or deleted here. A folder that a download is still saving into cannot be changed until the download finishes. Jellyfin treats renamed or moved videos as new items, so their watched marks may start again; Jellyfin shows the changes after its library scan (a minute or two).

## Settings (administrator)

| Setting | What it does |
| --- | --- |
| Update yt-dlp and deno every week | Sites change often; old yt-dlp versions stop working. **Update yt-dlp and deno now** does it at once. |
| Prefer H.264 in MP4 | Most TVs play it without Jellyfin converting it. |
| Default highest quality | Preselected on the Download tabs. |
| Write title, description and date (.nfo) | Jellyfin reads them for the item's details. |
| Scan the libraries after each download | So the download appears in Jellyfin straight away. |
| Use deno | YouTube and some other sites need a JavaScript runtime for most qualities. |
| YouTube player types | If YouTube refuses with 403, try another, for example `default,web_safari` or `default,mweb`. |
| Use this yt-dlp program instead | A yt-dlp you manage yourself (full path). |
| Extra yt-dlp options | One per line, for advanced use. |
| Show a link to the downloader page in Jellyfin's side menu | On by default; everyone sees it. Reload Jellyfin's page after changing it. |
| Name of the menu link | The link's text. |
| People who may download | See below. |

### People who may download

Users who are not administrators cannot open the dashboard, so they use the page at `/MediaDownloader`. For each user you allow:

1. Choose the user and give one or more **folders**. Each must be a library folder or a folder inside one, for example `C:\MOVIES\movies\Babui`; it is made on the first download.
2. Choose whether they may **delete** (files on My files, folders on My folders).
3. **Save settings**.
4. Give the user access to that library too: Dashboard › **Users** › the user › **Library access**, so they can play what they download.

They see only their own downloads and files, and can only download into, move between and delete in their own folders. Someone not allowed who opens the page is told to ask the administrator.

## The side-menu link

Jellyfin's web page reads its menu links from its `web/config.json`. The plugin adds its link to that file **as Jellyfin sends it**, so no Jellyfin file is changed and the link stays after Jellyfin updates. A browser that loaded Jellyfin before the plugin was installed may keep its old copy: reload with **Ctrl+Shift+R** (or close and reopen the app on a phone or TV).
