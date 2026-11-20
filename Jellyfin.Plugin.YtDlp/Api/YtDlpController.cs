using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.YtDlp.Api;

/// <summary>
/// The Media Downloader page's API. Administrators only.
/// </summary>
[ApiController]
[Route("YtDlp")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class YtDlpController : ControllerBase
{
    private readonly DownloadQueue _queue;
    private readonly YtDlpBinary _binary;
    private readonly DenoBinary _deno;
    private readonly UrlChecker _checker;
    private readonly StorageService _storage;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpController"/> class.
    /// </summary>
    /// <param name="queue">The download queue.</param>
    /// <param name="binary">The yt-dlp program keeper.</param>
    /// <param name="deno">The deno keeper.</param>
    /// <param name="checker">The link checker.</param>
    /// <param name="storage">The storage service.</param>
    /// <param name="userManager">User manager.</param>
    public YtDlpController(DownloadQueue queue, YtDlpBinary binary, DenoBinary deno, UrlChecker checker, StorageService storage, IUserManager userManager)
    {
        _userManager = userManager;
        _queue = queue;
        _binary = binary;
        _deno = deno;
        _checker = checker;
        _storage = storage;
    }

    /// <summary>Lists the libraries' folders.</summary>
    /// <returns>The folders.</returns>
    [HttpGet("Folders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetFolders()
    {
        return Ok(_queue.GetLibraryFolders().Select(f => new { library = f.Library, folder = f.Folder, type = f.Type }));
    }

    /// <summary>Shows yt-dlp's version and where it runs from.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetStatus(CancellationToken cancellationToken)
    {
        var path = YtDlpBinary.EffectivePath;
        string version;
        try
        {
            version = await YtDlpBinary.GetVersionAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            version = "does not run: " + ex.Message;
        }

        string deno;
        try
        {
            deno = await DenoBinary.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            deno = "does not run: " + ex.Message;
        }

        var sites = 0;
        try
        {
            sites = await _checker.CountHandlersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException)
        {
            sites = 0;
        }

        return Ok(new { path, version, deno, sites, account = Environment.UserName });
    }

    /// <summary>Downloads the latest yt-dlp and deno.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The new version.</returns>
    [HttpPost("Update")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> Update(CancellationToken cancellationToken)
    {
        try
        {
            var version = YtDlpBinary.EffectivePath == YtDlpBinary.ManagedPath
                ? await _binary.UpdateAsync(cancellationToken).ConfigureAwait(false)
                : "custom yt-dlp (update it yourself)";
            var deno = Plugin.Instance?.Configuration.UseDeno == true && DenoBinary.AssetName is not null
                ? await _deno.UpdateAsync(cancellationToken).ConfigureAwait(false)
                : "not used";
            return Ok(new { version, deno });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or System.IO.IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.IO.InvalidDataException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Checks a link without downloading: is it supported, by which site handler, and what is there.</summary>
    /// <param name="request">The link (only Url is used).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The check.</returns>
    [HttpPost("Check")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LinkCheck>> Check([FromBody] DownloadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _checker.CheckAsync(request.Url, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or System.IO.IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Ok(new LinkCheck { Result = "error", Message = ex.Message });
        }
    }

    /// <summary>Shows the drives, the library folders' sizes and every file with its size and who watched it.</summary>
    /// <returns>The report.</returns>
    [HttpGet("Storage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageReport>> GetStorage()
    {
        return Ok(await Task.Run(() => _storage.Build()).ConfigureAwait(false));
    }

    /// <summary>Deletes files from the library folders (through Jellyfin, so the library stays right).</summary>
    /// <param name="request">The paths.</param>
    /// <returns>One result per path.</returns>
    [HttpPost("Storage/Delete")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<DeleteResult>>> DeleteFiles([FromBody] DeleteRequest request)
    {
        var paths = request.Paths ?? new List<string>();
        return Ok(await Task.Run(() => _storage.Delete(paths)).ConfigureAwait(false));
    }

    /// <summary>Lists Jellyfin's users, for choosing who may use the downloader page.</summary>
    /// <returns>The users.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<object>> GetUsers()
    {
        return Ok(_userManager.GetUsers()
            .Select(u => (User: u, Dto: _userManager.GetUserDto(u, string.Empty)))
            .Select(x => new { id = x.User.Id.ToString("N"), name = x.User.Username, isAdmin = x.Dto.Policy?.IsAdministrator == true })
            .OrderBy(u => u.name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Lists downloads, newest first.</summary>
    /// <returns>The jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<DownloadJob>> GetJobs()
    {
        return Ok(_queue.GetJobs());
    }

    /// <summary>Adds a download.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The job.</returns>
    [HttpPost("Jobs")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<DownloadJob> AddJob([FromBody] DownloadRequest request)
    {
        try
        {
            var userId = UserAccessInfo.GetUserId(User);
            var name = userId is { } id ? _userManager.GetUserById(id)?.Username ?? string.Empty : string.Empty;
            return Ok(_queue.Enqueue(request, userId?.ToString("N") ?? string.Empty, name));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Lists a download's files with their Jellyfin item ids, for "Open in Jellyfin" links.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The files and item ids.</returns>
    [HttpGet("Jobs/{id}/Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<object>> GetJobItems([FromRoute] string id)
    {
        var items = _queue.GetJobItems(id);
        if (items is null)
        {
            return NotFound();
        }

        return Ok(items.Select(i => new { file = i.File, itemId = i.ItemId, name = i.Name }));
    }

    /// <summary>Cancels a download.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/{id}/Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Cancel([FromRoute] string id)
    {
        return _queue.Cancel(id) ? NoContent() : NotFound();
    }

    /// <summary>Removes finished downloads from the list.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Jobs/ClearFinished")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearFinished()
    {
        _queue.ClearFinished();
        return NoContent();
    }
}

/// <summary>Files to delete.</summary>
public class DeleteRequest
{
    /// <summary>Gets or sets the full paths.</summary>
    public List<string>? Paths { get; set; }
}
