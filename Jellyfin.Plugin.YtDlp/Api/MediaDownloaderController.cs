using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.YtDlp.Api;

/// <summary>
/// The downloader page for everyone the administrator allows (outside the dashboard), at /MediaDownloader.
/// Administrators may use everything; other users only the folders the administrator gave them.
/// </summary>
[ApiController]
[Route("MediaDownloader")]
public class MediaDownloaderController : ControllerBase
{
    private readonly DownloadQueue _queue;
    private readonly UrlChecker _checker;
    private readonly StorageService _storage;
    private readonly IUserManager _userManager;
    private readonly FolderService _folders;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDownloaderController"/> class.
    /// </summary>
    /// <param name="queue">The download queue.</param>
    /// <param name="checker">The link checker.</param>
    /// <param name="storage">The storage service.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="folders">The folder service.</param>
    public MediaDownloaderController(DownloadQueue queue, UrlChecker checker, StorageService storage, IUserManager userManager, FolderService folders)
    {
        _folders = folders;
        _queue = queue;
        _checker = checker;
        _storage = storage;
        _userManager = userManager;
    }

    /// <summary>The page itself (it signs in with the user's own Jellyfin account).</summary>
    /// <returns>The HTML page.</returns>
    [HttpGet]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult Page()
    {
        // The page loads its script by a relative link, so it must be opened without a trailing slash.
        var path = Request.PathBase.Add(Request.Path).Value ?? string.Empty;
        if (path.EndsWith('/'))
        {
            return Redirect(path.TrimEnd('/') + Request.QueryString);
        }

        return Resource("user.html", "text/html; charset=utf-8");
    }

    /// <summary>The page's script.</summary>
    /// <returns>The script.</returns>
    [HttpGet("app.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult Script() => Resource("user.js", "application/javascript; charset=utf-8");

    /// <summary>The subtitle language picker, used by this page and the dashboard page.</summary>
    /// <returns>The script.</returns>
    [HttpGet("subs.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult SubtitlesScript() => Resource("subs.js", "application/javascript; charset=utf-8");

    /// <summary>Who is signed in, and what they may use.</summary>
    /// <returns>The access.</returns>
    [HttpGet("Me")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Me()
    {
        var access = UserAccessInfo.Resolve(User, _userManager, _queue);
        if (access is null)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            access.UserName,
            access.IsAdmin,
            Allowed = access.Allowed,
            access.CanDelete,
            Folders = access.Folders.Select(f => new { Path = f.Path, Label = f.Label }),
            DefaultMaxHeight = config?.DefaultMaxHeight ?? 1080
        });
    }

    /// <summary>Checks a link without downloading.</summary>
    /// <param name="request">The link (only Url is used).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The check.</returns>
    [HttpPost("Check")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<LinkCheck>> Check([FromBody] DownloadRequest request, CancellationToken cancellationToken)
    {
        if (Denied(out _) is { } denied)
        {
            return denied;
        }

        try
        {
            return Ok(await _checker.CheckAsync(request.Url, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Ok(new LinkCheck { Result = "error", Message = ex.Message });
        }
    }

    /// <summary>Adds a download into one of the user's folders.</summary>
    /// <param name="request">The request; TargetFolder must be one of the user's folders.</param>
    /// <returns>The job.</returns>
    [HttpPost("Jobs")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<DownloadJob> AddJob([FromBody] DownloadRequest request)
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        // The folder may be one of the user's folders or any folder inside them.
        var chosen = UserAccessInfo.NormalizePath(request.TargetFolder);
        var target = access!.FindContaining(chosen);
        if (target is null)
        {
            return BadRequest(new { error = "Choose one of your folders." });
        }

        // The queue takes a library folder plus a sub folder; the user's folder may itself be a sub folder of a library.
        var relative = Path.GetRelativePath(target.LibraryFolder, chosen);
        var sub = (request.SubFolder ?? string.Empty).Trim().Trim('/', '\\');
        if (sub.Split('/', '\\').Any(p => p == "." || p == ".."))
        {
            return BadRequest(new { error = "The sub folder must stay inside your folder." });
        }

        request.TargetFolder = target.LibraryFolder;
        request.SubFolder = relative == "." ? sub : (sub.Length == 0 ? relative : Path.Combine(relative, sub));
        try
        {
            return Ok(_queue.Enqueue(request, access.UserId, access.UserName));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Lists the user's own downloads, newest first.</summary>
    /// <returns>The jobs.</returns>
    [HttpGet("Jobs")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<IEnumerable<DownloadJob>> GetJobs()
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        return Ok(_queue.GetJobs().Where(j => j.OwnerId == access!.UserId));
    }

    /// <summary>Lists a download's files with their Jellyfin item ids.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The files and item ids.</returns>
    [HttpGet("Jobs/{id}/Items")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<IEnumerable<object>> GetJobItems([FromRoute] string id)
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        if (_queue.GetJob(id) is not { } job || job.OwnerId != access!.UserId)
        {
            return NotFound();
        }

        var items = _queue.GetJobItems(id) ?? Array.Empty<(string File, string? ItemId, string? Name)>();
        return Ok(items.Select(i => new { file = i.File, itemId = i.ItemId, name = i.Name }));
    }

    /// <summary>Cancels one of the user's downloads.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/{id}/Cancel")]
    [Authorize]
    public ActionResult Cancel([FromRoute] string id)
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        if (_queue.GetJob(id) is not { } job || job.OwnerId != access!.UserId)
        {
            return NotFound();
        }

        _queue.Cancel(id);
        return NoContent();
    }

    /// <summary>Removes the user's finished downloads from the list.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/ClearFinished")]
    [Authorize]
    public ActionResult ClearFinished()
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        _queue.ClearFinished(access!.UserId);
        return NoContent();
    }

    /// <summary>Shows the user's folders: sizes, free space and files (with the user's own watched state).</summary>
    /// <returns>The report.</returns>
    [HttpGet("Storage")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<StorageReport>> GetStorage()
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        var full = await Task.Run(() => _storage.Build()).ConfigureAwait(false);
        var report = new StorageReport();
        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in access!.Folders)
        {
            var files = full.Files.Where(f => folder.Contains(f.Path)).ToList();
            var space = new FolderSpace
            {
                Library = folder.Library,
                Folder = folder.Path,
                Size = await Task.Run(() => StorageService.FolderBytes(folder.Path)).ConfigureAwait(false),
                MediaFiles = files.Count(f => f.Media),
                Drive = Path.GetPathRoot(folder.Path) ?? string.Empty
            };
            report.Folders.Add(space);
            drives.Add(space.Drive);
            foreach (var f in files)
            {
                if (report.Files.Any(x => x.Path == f.Path))
                {
                    continue;
                }

                var copy = new StoredFile
                {
                    Path = f.Path,
                    Relative = Path.GetRelativePath(folder.Path, f.Path),
                    Library = folder.Label,
                    Size = f.Size,
                    Added = f.Added,
                    Media = f.Media,
                    ItemId = f.ItemId,
                    Title = f.Title
                };
                copy.Watched.AddRange(f.Watched.Where(w => string.Equals(w.User, access.UserName, StringComparison.OrdinalIgnoreCase)));
                report.Files.Add(copy);
            }
        }

        report.Drives.AddRange(full.Drives.Where(d => drives.Contains(d.Name)));
        report.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
        return Ok(report);
    }

    /// <summary>Deletes files inside the user's folders.</summary>
    /// <param name="request">The paths.</param>
    /// <returns>One result per path.</returns>
    [HttpPost("Storage/Delete")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<IEnumerable<DeleteResult>>> DeleteFiles([FromBody] DeleteRequest request)
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        if (!access!.CanDelete)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You may not delete files. Ask the server's administrator." });
        }

        var results = new List<DeleteResult>();
        var allowed = new List<string>();
        foreach (var raw in (request.Paths ?? new List<string>()).Distinct())
        {
            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                results.Add(new DeleteResult { Path = raw, Error = "Not a valid path." });
                continue;
            }

            if (access.Folders.Any(f => f.Contains(full)))
            {
                allowed.Add(full);
            }
            else
            {
                results.Add(new DeleteResult { Path = raw, Error = "Only files in your own folders can be deleted." });
            }
        }

        var roots = access.Folders.Select(f => f.Path).ToList();
        results.AddRange(await Task.Run(() => _storage.Delete(allowed, roots)).ConfigureAwait(false));
        return Ok(results);
    }

    /// <summary>Lists the user's folders and every folder inside them.</summary>
    /// <returns>The folders.</returns>
    [HttpGet("Folders")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<IEnumerable<object>>> GetFolders()
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        var list = new List<object>();
        foreach (var root in access!.Folders)
        {
            var entries = await Task.Run(() => _folders.List(root.Path)).ConfigureAwait(false);
            list.AddRange(entries.Select(e => new
            {
                e.Path,
                e.Relative,
                e.Root,
                RootLabel = root.Label,
                e.Depth,
                e.Files,
                e.Size,
                Exists = Directory.Exists(e.Path)
            }));
        }

        return Ok(list);
    }

    /// <summary>Creates a folder inside one of the user's folders.</summary>
    /// <param name="request">Path: the folder to create it in; Name: the new folder's name.</param>
    /// <returns>The result.</returns>
    [HttpPost("Folders/Create")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<FolderResult> CreateFolder([FromBody] FolderRequest request)
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        var parent = UserAccessInfo.NormalizePath(request.Path);
        var root = access!.FindContaining(parent);
        if (root is null)
        {
            return BadRequest(new { error = "Only folders inside your own folders can be changed." });
        }

        if (access.IsRoot(parent) && !Directory.Exists(parent))
        {
            // The user's own folder is made on the first use.
            try
            {
                Directory.CreateDirectory(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Ok(new FolderResult { Path = parent, Error = ex.Message });
            }
        }

        return Ok(_folders.Create(parent, request.Name));
    }

    /// <summary>Renames a folder inside one of the user's folders.</summary>
    /// <param name="request">Path: the folder; Name: its new name.</param>
    /// <returns>The result.</returns>
    [HttpPost("Folders/Rename")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<FolderResult> RenameFolder([FromBody] FolderRequest request)
    {
        if (CheckInner(request.Path, out var path) is { } bad)
        {
            return bad;
        }

        return Ok(_folders.Rename(path, request.Name));
    }

    /// <summary>Moves a folder into another of the user's folders.</summary>
    /// <param name="request">Path: the folder; Target: the folder to move it into.</param>
    /// <returns>The result.</returns>
    [HttpPost("Folders/Move")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<FolderResult> MoveFolder([FromBody] FolderRequest request)
    {
        if (CheckInner(request.Path, out var path) is { } bad)
        {
            return bad;
        }

        Denied(out var access);
        var into = UserAccessInfo.NormalizePath(request.Target);
        if (access!.FindContaining(into) is null)
        {
            return BadRequest(new { error = "Choose one of your folders to move it into." });
        }

        return Ok(_folders.MoveFolder(path, into));
    }

    /// <summary>Deletes a folder inside one of the user's folders, with everything in it.</summary>
    /// <param name="request">Path: the folder.</param>
    /// <returns>The result.</returns>
    [HttpPost("Folders/Delete")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<FolderResult> DeleteFolder([FromBody] FolderRequest request)
    {
        if (CheckInner(request.Path, out var path) is { } bad)
        {
            return bad;
        }

        Denied(out var access);
        if (!access!.CanDelete)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You may not delete. Ask the server's administrator." });
        }

        return Ok(_folders.Delete(path));
    }

    /// <summary>Moves files into another of the user's folders.</summary>
    /// <param name="request">Paths: the files; Target: the folder to move them into.</param>
    /// <returns>One result per file.</returns>
    [HttpPost("Storage/Move")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<IEnumerable<FolderResult>>> MoveFiles([FromBody] MoveRequest request)
    {
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        var into = UserAccessInfo.NormalizePath(request.Target);
        if (access!.FindContaining(into) is null)
        {
            return BadRequest(new { error = "Choose one of your folders to move them into." });
        }

        var results = new List<FolderResult>();
        var allowed = new List<string>();
        foreach (var raw in (request.Paths ?? new List<string>()).Distinct())
        {
            var full = UserAccessInfo.NormalizePath(raw);
            if (full.Length > 0 && access.Folders.Any(f => f.Contains(full)))
            {
                allowed.Add(full);
            }
            else
            {
                results.Add(new FolderResult { Path = raw, Error = "Only files in your own folders can be moved." });
            }
        }

        var roots = access.Folders.Select(f => f.Path).ToList();
        results.AddRange(await Task.Run(() => _folders.MoveFiles(allowed, into, roots)).ConfigureAwait(false));
        return Ok(results);
    }

    private ActionResult? CheckInner(string? raw, out string path)
    {
        var full = UserAccessInfo.NormalizePath(raw);
        path = full;
        if (Denied(out var access) is { } denied)
        {
            return denied;
        }

        if (access!.IsRoot(full))
        {
            return BadRequest(new { error = "Your own folder itself cannot be renamed, moved or deleted here; only the folders inside it. Ask the server's administrator." });
        }

        if (full.Length == 0 || !access.Folders.Any(f => f.Contains(full)))
        {
            return BadRequest(new { error = "Only folders inside your own folders can be changed." });
        }

        return null;
    }

    private ActionResult? Denied(out UserAccessInfo? access)
    {
        access = UserAccessInfo.Resolve(User, _userManager, _queue);
        if (access is null)
        {
            return Unauthorized();
        }

        if (!access.Allowed)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Your account may not use the downloader. Ask the server's administrator to allow it (Dashboard > Media Downloader > Settings)." });
        }

        return null;
    }

    private ActionResult Resource(string name, string contentType)
    {
        var stream = GetType().Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web." + name);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, contentType);
    }
}

/// <summary>A folder change from the page.</summary>
public class FolderRequest
{
    /// <summary>Gets or sets the folder.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the new name (create, rename).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the folder to move into (move).</summary>
    public string? Target { get; set; }
}

/// <summary>Files to move.</summary>
public class MoveRequest
{
    /// <summary>Gets or sets the files.</summary>
    public List<string>? Paths { get; set; }

    /// <summary>Gets or sets the folder to move them into.</summary>
    public string? Target { get; set; }
}
