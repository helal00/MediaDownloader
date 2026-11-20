using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>What a download is saved as.</summary>
public enum DownloadKind
{
    /// <summary>A film: its own folder "Title (Year)", poster and movie .nfo.</summary>
    Movie,

    /// <summary>A series episode: "Channel/Season YYYY/Channel - YYYY-MM-DD - Title", episode .nfo.</summary>
    Episode,

    /// <summary>Music: audio only, "Artist/Album/Title.m4a".</summary>
    Music,

    /// <summary>A plain video file "Title.ext" in the chosen folder.</summary>
    Video
}

/// <summary>State of a download.</summary>
public enum JobState
{
    /// <summary>Waiting.</summary>
    Queued,

    /// <summary>Downloading.</summary>
    Running,

    /// <summary>Finished.</summary>
    Done,

    /// <summary>Failed.</summary>
    Failed,

    /// <summary>Cancelled.</summary>
    Cancelled
}

/// <summary>A download request from the page.</summary>
public class DownloadRequest
{
    /// <summary>Gets or sets the video, playlist or channel link.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the library folder to save into (one of the libraries' folders).</summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional sub folder inside the library folder.</summary>
    public string SubFolder { get; set; } = string.Empty;

    /// <summary>Gets or sets what the download is saved as.</summary>
    public DownloadKind Kind { get; set; } = DownloadKind.Movie;

    /// <summary>Gets or sets the highest video height (0: best).</summary>
    public int MaxHeight { get; set; } = 1080;

    /// <summary>Gets or sets a value indicating whether a whole playlist or channel is downloaded.</summary>
    public bool WholePlaylist { get; set; }

    /// <summary>Gets or sets a value indicating whether videos downloaded before are skipped.</summary>
    public bool SkipDownloaded { get; set; } = true;

    /// <summary>Gets or sets the subtitle languages to save, as yt-dlp's language codes (for example "en", "bn", "pt-BR"); empty for none.</summary>
    public List<string> Subtitles { get; set; } = new();
}

/// <summary>What happened to the subtitles of a download, counted per language.</summary>
public class SubtitleTally
{
    private static readonly Regex Offered = new(@"^\[info\] .+?: Downloading subtitles: (.+)$", RegexOptions.Compiled);
    private static readonly Regex Written = new(@"^\[info\] Writing video subtitles to: (.+)$", RegexOptions.Compiled);
    private static readonly Regex Present = new(@"^\[info\] Video subtitle (.+)\.[^.]+ is already present$", RegexOptions.Compiled);
    private static readonly Regex Refused = new(@"Unable to download video subtitles for '(.+?)': (.+)$", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _offered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _reason = new(StringComparer.OrdinalIgnoreCase);
    private int _videos;

    /// <summary>Counts a video that is being downloaded.</summary>
    public void AddVideo()
    {
        lock (_lock)
        {
            _videos++;
        }
    }

    /// <summary>Reads one line of yt-dlp's output.</summary>
    /// <param name="line">The line.</param>
    public void Read(string line)
    {
        lock (_lock)
        {
            Match m;
            if ((m = Offered.Match(line)).Success)
            {
                foreach (var lang in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _offered[lang] = _offered.GetValueOrDefault(lang) + 1;
                }
            }
            else if ((m = Written.Match(line)).Success)
            {
                // The file is "<name>.<language>.<format>".
                var name = Path.GetFileNameWithoutExtension(m.Groups[1].Value.Trim());
                var lang = Path.GetExtension(name).TrimStart('.');
                if (lang.Length > 0)
                {
                    _saved[lang] = _saved.GetValueOrDefault(lang) + 1;
                }
            }
            else if ((m = Present.Match(line)).Success)
            {
                _saved[m.Groups[1].Value] = _saved.GetValueOrDefault(m.Groups[1].Value) + 1;
            }
            else if ((m = Refused.Match(line)).Success)
            {
                var lang = m.Groups[1].Value;
                _refused[lang] = _refused.GetValueOrDefault(lang) + 1;
                _saved[lang] = Math.Max(0, _saved.GetValueOrDefault(lang) - 1);
                var reason = m.Groups[2].Value.Trim();
                _reason[lang] = reason.Length > 120 ? reason[..120] + "..." : reason;
            }
        }
    }

    /// <summary>Gets the languages saved for at least one video.</summary>
    /// <param name="requested">The languages asked for, in the user's order.</param>
    /// <returns>The languages.</returns>
    public List<string> SavedLanguages(IReadOnlyList<string> requested)
    {
        lock (_lock)
        {
            return requested.Where(l => _saved.GetValueOrDefault(l) > 0).ToList();
        }
    }

    /// <summary>Describes the languages that were skipped, or gives an empty text when none were.</summary>
    /// <param name="requested">The languages asked for, in the user's order.</param>
    /// <returns>The text.</returns>
    public string SkippedText(IReadOnlyList<string> requested)
    {
        lock (_lock)
        {
            if (_videos == 0)
            {
                return string.Empty;
            }

            var many = _videos > 1;
            string Count(int n) => many ? $" for {n} of {_videos} videos" : string.Empty;
            var parts = new List<string>();
            foreach (var lang in requested)
            {
                var refused = _refused.GetValueOrDefault(lang);
                if (refused > 0)
                {
                    parts.Add($"{lang} refused by the site{Count(refused)} ({_reason.GetValueOrDefault(lang)})");
                }

                var missing = Math.Max(0, _videos - _offered.GetValueOrDefault(lang));
                if (missing > 0)
                {
                    parts.Add($"{lang} not offered{(many ? Count(missing) : " for this video")}");
                }
            }

            if (parts.Count == 0)
            {
                return string.Empty;
            }

            return "Subtitles skipped: " + string.Join("; ", parts) + ". " + (many ? "The videos were saved." : "The video was saved.");
        }
    }
}

/// <summary>A download and its progress.</summary>
public class DownloadJob
{
    /// <summary>Gets the job id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the request.</summary>
    public DownloadRequest Request { get; init; } = new();

    /// <summary>Gets or sets the state.</summary>
    public JobState State { get; set; } = JobState.Queued;

    /// <summary>Gets or sets the current title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the current item's percentage.</summary>
    public double Percent { get; set; }

    /// <summary>Gets or sets the speed text.</summary>
    public string Speed { get; set; } = string.Empty;

    /// <summary>Gets or sets the time left text.</summary>
    public string Eta { get; set; } = string.Empty;

    /// <summary>Gets the finished files.</summary>
    public List<string> Files { get; } = new();

    /// <summary>Gets or sets the error.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Gets or sets a note about a finished download that worked only in part (for example subtitles skipped).</summary>
    public string Warning { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle languages that were saved.</summary>
    public List<string> SubtitlesSaved { get; set; } = new();

    /// <summary>Gets the subtitle counts (not sent to the page).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SubtitleTally SubtitleTally { get; } = new();

    /// <summary>Gets the last lines of yt-dlp's output.</summary>
    public List<string> Log { get; } = new();

    /// <summary>Gets the time it was added.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets the time it ended.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the id of the user who asked for it (empty for older jobs).</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the user who asked for it.</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the running process (not sent to the page).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Process? Process { get; set; }

    /// <summary>Gets or sets a value indicating whether cancel was asked.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CancelRequested { get; set; }
}

/// <summary>
/// Runs downloads one at a time.
/// </summary>
public class DownloadQueue
{
    private const int MaxJobs = 100;
    private const int MaxLogLines = 40;
    private const int MaxSubtitleLanguages = 20;
    private static readonly Regex LanguageCode = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,34}$", RegexOptions.Compiled);

    private readonly ConcurrentQueue<DownloadJob> _pending = new();
    private readonly List<DownloadJob> _jobs = new();
    private readonly object _jobsLock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly YtDlpBinary _binary;
    private readonly DenoBinary _deno;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<DownloadQueue> _logger;
    private int _workerStarted;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadQueue"/> class.
    /// </summary>
    /// <param name="binary">The yt-dlp program keeper.</param>
    /// <param name="deno">The deno keeper.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaEncoder">Media encoder (for Jellyfin's own ffmpeg).</param>
    /// <param name="logger">Logger.</param>
    public DownloadQueue(YtDlpBinary binary, DenoBinary deno, ILibraryManager libraryManager, IMediaEncoder mediaEncoder, ILogger<DownloadQueue> logger)
    {
        _binary = binary;
        _deno = deno;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>Gets all library folders, as (library name, folder).</summary>
    /// <returns>The folders.</returns>
    public IReadOnlyList<(string Library, string Folder, string Type)> GetLibraryFolders()
    {
        var result = new List<(string, string, string)>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var location in folder.Locations ?? Array.Empty<string>())
            {
                result.Add((folder.Name, location, folder.CollectionType?.ToString() ?? string.Empty));
            }
        }

        return result;
    }

    /// <summary>Gets a copy of the job list, newest first.</summary>
    /// <returns>The jobs.</returns>
    public IReadOnlyList<DownloadJob> GetJobs()
    {
        lock (_jobsLock)
        {
            return _jobs.OrderByDescending(j => j.CreatedAt).ToList();
        }
    }

    /// <summary>Adds a download.</summary>
    /// <param name="request">The request.</param>
    /// <param name="ownerId">The id of the user who asks for it.</param>
    /// <param name="ownerName">The name of the user who asks for it.</param>
    /// <returns>The job.</returns>
    public DownloadJob Enqueue(DownloadRequest request, string ownerId = "", string ownerName = "")
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Give a full http(s) link.");
        }

        request.Url = uri.ToString();
        request.Subtitles = request.Kind == DownloadKind.Music ? new List<string>() : CleanSubtitles(request.Subtitles);
        request.TargetFolder = ResolveTarget(request.TargetFolder, request.SubFolder);

        var job = new DownloadJob { Request = request, OwnerId = ownerId, OwnerName = ownerName };
        lock (_jobsLock)
        {
            _jobs.Add(job);
            var finished = _jobs.Where(j => j.State is JobState.Done or JobState.Failed or JobState.Cancelled)
                .OrderBy(j => j.CreatedAt).ToList();
            for (var i = 0; _jobs.Count > MaxJobs && i < finished.Count; i++)
            {
                _jobs.Remove(finished[i]);
            }
        }

        _pending.Enqueue(job);
        _signal.Release();
        if (Interlocked.Exchange(ref _workerStarted, 1) == 0)
        {
            _ = Task.Run(WorkerAsync);
        }

        return job;
    }

    /// <summary>Cancels a job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>Whether the job was found.</returns>
    public bool Cancel(string id)
    {
        DownloadJob? job;
        lock (_jobsLock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
        }

        if (job is null)
        {
            return false;
        }

        job.CancelRequested = true;
        if (job.State == JobState.Queued)
        {
            job.State = JobState.Cancelled;
            job.FinishedAt = DateTime.UtcNow;
        }

        try
        {
            if (job.Process is { HasExited: false } p)
            {
                p.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // already ended
        }

        return true;
    }

    /// <summary>Finds the Jellyfin items of a job's finished files (null id while the library scan has not added a file yet).</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The files with their item ids, or null when the job is unknown.</returns>
    public IReadOnlyList<(string File, string? ItemId, string? Name)>? GetJobItems(string id)
    {
        DownloadJob? job;
        lock (_jobsLock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
        }

        if (job is null)
        {
            return null;
        }

        List<string> files;
        lock (job.Files)
        {
            files = job.Files.ToList();
        }

        var result = new List<(string, string?, string?)>();
        foreach (var file in files)
        {
            var item = _libraryManager.FindByPath(file, false);
            result.Add((file, item?.Id.ToString("N"), item?.Name));
        }

        return result;
    }

    /// <summary>Finds a job.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job, or null.</returns>
    public DownloadJob? GetJob(string id)
    {
        lock (_jobsLock)
        {
            return _jobs.FirstOrDefault(j => j.Id == id);
        }
    }

    /// <summary>Removes finished jobs from the list.</summary>
    /// <param name="ownerId">Only this user's jobs (null: everyone's).</param>
    public void ClearFinished(string? ownerId = null)
    {
        lock (_jobsLock)
        {
            _jobs.RemoveAll(j => j.State is JobState.Done or JobState.Failed or JobState.Cancelled
                && (ownerId is null || j.OwnerId == ownerId));
        }
    }

    private string ResolveTarget(string targetFolder, string subFolder)
    {
        var folders = GetLibraryFolders().Select(f => Path.GetFullPath(f.Folder)).ToList();
        var target = Path.GetFullPath(targetFolder ?? string.Empty);
        if (!folders.Any(f => string.Equals(f.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Choose one of the libraries' folders.");
        }

        var sub = (subFolder ?? string.Empty).Trim().Trim('/', '\\');
        if (sub.Length == 0)
        {
            return target;
        }

        var parts = sub.Split('/', '\\').Where(p => p.Length > 0).ToArray();
        if (parts.Any(p => p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0))
        {
            throw new ArgumentException("The sub folder name has characters that cannot be used in folder names.");
        }

        var full = Path.GetFullPath(Path.Combine(new[] { target }.Concat(parts).ToArray()));
        if (!full.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The sub folder must stay inside the library folder.");
        }

        return full;
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            if (!_pending.TryDequeue(out var job) || job.State == JobState.Cancelled)
            {
                continue;
            }

            try
            {
                await RunAsync(job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                job.State = job.CancelRequested ? JobState.Cancelled : JobState.Failed;
                job.Error = job.CancelRequested ? string.Empty : ex.Message;
                job.FinishedAt = DateTime.UtcNow;
                _logger.LogError(ex, "Download {Url} failed", job.Request.Url);
            }
            finally
            {
                job.Process = null;
            }
        }
    }

    private async Task RunAsync(DownloadJob job)
    {
        job.State = JobState.Running;
        var config = Plugin.Instance!.Configuration;
        var program = await _binary.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
        string? deno = null;
        if (config.UseDeno)
        {
            try
            {
                deno = await _deno.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _logger.LogWarning(ex, "deno is not available; YouTube may offer fewer formats");
                AddLog(job, "WARNING: deno could not be installed: " + ex.Message);
            }
        }

        Directory.CreateDirectory(job.Request.TargetFolder);
        CheckWritable(job.Request.TargetFolder);

        var exitCode = await RunYtDlpAsync(job, program, config, deno).ConfigureAwait(false);
        if (job.Request.Subtitles.Count > 0)
        {
            job.SubtitlesSaved = job.SubtitleTally.SavedLanguages(job.Request.Subtitles);
            job.Warning = job.SubtitleTally.SkippedText(job.Request.Subtitles);
        }

        foreach (var file in job.Files.ToList())
        {
            FinishFile(file, job.Request.Kind, config.WriteNfo);
        }

        FinishLeftovers(job, config.WriteNfo);

        if (job.CancelRequested)
        {
            job.State = JobState.Cancelled;
        }
        else if (exitCode == 0 || job.Files.Count > 0)
        {
            job.State = JobState.Done;
            job.Percent = 100;
            if (exitCode != 0)
            {
                job.Error = "Some items failed; see the log lines.";
            }
            else if (job.Files.Count == 0)
            {
                job.Error = "Nothing new to download (already downloaded before).";
            }
        }
        else
        {
            job.State = JobState.Failed;
            lock (job.Log)
            {
                job.Error = job.Log.LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal)) ?? $"yt-dlp ended with code {exitCode}";
            }
        }

        job.FinishedAt = DateTime.UtcNow;
        if (job.Files.Count > 0 && config.ScanAfterDownload)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    private async Task<int> RunYtDlpAsync(DownloadJob job, string program, Configuration.PluginConfiguration config, string? deno)
    {
        var psi = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var arg in BuildArguments(job.Request, config, deno))
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogInformation("yt-dlp: {Url} -> {Folder} ({Kind}, subtitles: {Subs})", job.Request.Url, job.Request.TargetFolder, job.Request.Kind, job.Request.Subtitles.Count > 0 ? string.Join(",", job.Request.Subtitles) : "none");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp did not start");
        job.Process = process;
        if (job.CancelRequested)
        {
            process.Kill(true);
        }

        var stdout = ReadLinesAsync(process.StandardOutput, job);
        var stderr = ReadLinesAsync(process.StandardError, job);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        job.Process = null;
        return process.ExitCode;
    }

    /// <summary>Checks and tidies the subtitle languages of a request.</summary>
    /// <param name="languages">The languages from the page.</param>
    /// <returns>The languages, without repeats.</returns>
    public static List<string> CleanSubtitles(IEnumerable<string>? languages)
    {
        var result = new List<string>();
        foreach (var raw in languages ?? Enumerable.Empty<string>())
        {
            var lang = (raw ?? string.Empty).Trim();
            if (lang.Length == 0)
            {
                continue;
            }

            // yt-dlp reads each one as a pattern, so only plain language codes are let through.
            if (!LanguageCode.IsMatch(lang))
            {
                throw new ArgumentException($"\"{lang}\" is not a subtitle language code (for example en, bn or pt-BR).");
            }

            if (!result.Contains(lang, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(lang);
            }
        }

        if (result.Count > MaxSubtitleLanguages)
        {
            throw new ArgumentException($"Choose at most {MaxSubtitleLanguages} subtitle languages.");
        }

        return result;
    }

    private static void CheckWritable(string folder)
    {
        var probe = Path.Combine(folder, ".ytdlp-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            throw new InvalidOperationException(
                $"Jellyfin's service account ({Environment.UserName}) cannot write to {folder}. Give it Modify permission on the library folder (see the plugin's README).", ex);
        }
    }

    private List<string> BuildArguments(DownloadRequest request, Configuration.PluginConfiguration config, string? deno)
    {
        var data = YtDlpBinary.DataFolder;
        var args = new List<string>
        {
            "--newline", "--progress", "--no-simulate", "--no-colors", "--encoding", "utf-8",
            "--replace-in-metadata", "title", @"\s*[|\uFF5C]\s*", " - ",
            "--progress-template", "download:YTDLP_PROGRESS %(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--print", "before_dl:YTDLP_TITLE %(title)s",
            "--print", "after_move:YTDLP_FILE %(filepath)s",
            "--cache-dir", Path.Combine(data, "cache"),
            "-P", request.TargetFolder,
            "--no-mtime", "--windows-filenames", "--no-overwrites", "--continue",
            "--embed-metadata", "--embed-chapters",
            "--write-info-json", "--no-write-playlist-metafiles"
        };

        var ffmpeg = _mediaEncoder.EncoderPath;
        if (!string.IsNullOrEmpty(ffmpeg) && File.Exists(ffmpeg))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg);
        }

        if (!string.IsNullOrEmpty(deno))
        {
            args.Add("--js-runtimes");
            args.Add("deno:" + deno);
        }

        var clients = (config.YouTubePlayerClient ?? string.Empty).Trim();
        if (clients.Length > 0)
        {
            args.Add("--extractor-args");
            args.Add("youtube:player_client=" + clients);
        }

        args.Add(request.WholePlaylist ? "--yes-playlist" : "--no-playlist");
        if (request.SkipDownloaded)
        {
            args.Add("--download-archive");
            args.Add(Path.Combine(data, "downloaded.txt"));
        }

        const string Year = "%(upload_date>%Y|0000)s";
        const string Title = "%(title).150B";
        const string Channel = "%(channel,uploader|YouTube).100B";
        switch (request.Kind)
        {
            case DownloadKind.Music:
                args.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", "m4a", "--embed-thumbnail", "--convert-thumbnails", "jpg" });
                args.AddRange(new[] { "-o", "%(artist,channel,uploader|Unknown).100B/%(album,playlist_title|Singles).100B/" + Title + ".%(ext)s" });
                break;
            case DownloadKind.Episode:
                AddVideoFormat(args, request.MaxHeight, config);
                var episode = Channel + "/Season " + Year + "/" + Channel + " - %(upload_date>%Y-%m-%d|0000-00-00)s - %(title).120B";
                args.AddRange(new[] { "-o", episode + ".%(ext)s", "-o", "thumbnail:" + episode + "-thumb.%(ext)s" });
                AddThumbnailAndSubs(args, request.Subtitles);
                break;
            case DownloadKind.Video:
                AddVideoFormat(args, request.MaxHeight, config);
                args.AddRange(new[] { "-o", Title + ".%(ext)s", "-o", "thumbnail:" + Title + "-poster.%(ext)s" });
                AddThumbnailAndSubs(args, request.Subtitles);
                break;
            default:
                AddVideoFormat(args, request.MaxHeight, config);
                var movie = Title + " (" + Year + ")";
                args.AddRange(new[] { "-o", movie + "/" + movie + ".%(ext)s", "-o", "thumbnail:" + movie + "/poster.%(ext)s" });
                AddThumbnailAndSubs(args, request.Subtitles);
                break;
        }

        foreach (var line in (config.ExtraArguments ?? string.Empty).Split('\n'))
        {
            var extra = line.Trim();
            if (extra.Length > 0)
            {
                args.Add(extra);
            }
        }

        args.Add("--");
        args.Add(request.Url);
        return args;
    }

    private static void AddVideoFormat(List<string> args, int maxHeight, Configuration.PluginConfiguration config)
    {
        var res = maxHeight > 0 ? "res:" + maxHeight.ToString(CultureInfo.InvariantCulture) : "res";
        if (config.PreferTvCompatible)
        {
            args.AddRange(new[] { "-S", res + ",vcodec:h264,acodec:m4a", "--merge-output-format", "mp4/mkv" });
        }
        else
        {
            args.AddRange(new[] { "-S", res, "--merge-output-format", "mkv" });
        }
    }

    private static void AddThumbnailAndSubs(List<string> args, List<string> subtitles)
    {
        args.AddRange(new[] { "--write-thumbnail", "--convert-thumbnails", "jpg" });
        if (subtitles.Count > 0)
        {
            // Each language goes as a whole-name pattern. A short pause before each subtitle request makes
            // "429 Too Many Requests" less likely; --ignore-errors makes a refused subtitle a warning, so the video
            // and the other languages are still saved (other failures still end yt-dlp with an error code).
            var langs = string.Join(",", subtitles.Select(Regex.Escape));
            args.AddRange(new[] { "--write-subs", "--write-auto-subs", "--sub-langs", langs, "--convert-subs", "srt", "--sleep-subtitles", "2", "--ignore-errors" });
        }
    }

    private async Task ReadLinesAsync(StreamReader reader, DownloadJob job)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (line.StartsWith("YTDLP_PROGRESS ", StringComparison.Ordinal))
            {
                var parts = line.Substring(15).Split('|');
                if (parts.Length == 3)
                {
                    if (double.TryParse(parts[0].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    {
                        job.Percent = pct;
                    }

                    job.Speed = parts[1].Trim();
                    job.Eta = parts[2].Trim();
                }

                continue;
            }

            if (line.StartsWith("YTDLP_TITLE ", StringComparison.Ordinal))
            {
                job.SubtitleTally.AddVideo();
                job.Title = line.Substring(12).Trim();
                job.Percent = 0;
                continue;
            }

            if (line.StartsWith("YTDLP_FILE ", StringComparison.Ordinal))
            {
                lock (job.Files)
                {
                    job.Files.Add(line.Substring(11).Trim());
                }

                continue;
            }

            job.SubtitleTally.Read(line);
            AddLog(job, line);
        }
    }

    private static void AddLog(DownloadJob job, string line)
    {
        lock (job.Log)
        {
            job.Log.Add(line);
            if (job.Log.Count > MaxLogLines)
            {
                job.Log.RemoveAt(0);
            }
        }
    }

    private void FinishLeftovers(DownloadJob job, bool writeNfo)
    {
        // Info files whose video path yt-dlp did not report readably: pair each with the media file of the same name.
        string[] infos;
        try
        {
            infos = Directory.GetFiles(job.Request.TargetFolder, "*.info.json", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var info in infos.Where(i => File.GetLastWriteTimeUtc(i) >= job.CreatedAt.AddMinutes(-1)))
        {
            var basePath = info[..^".info.json".Length];
            var dir = Path.GetDirectoryName(info) ?? string.Empty;
            var media = Directory.GetFiles(dir)
                .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.Combine(dir, Path.GetFileNameWithoutExtension(f)), basePath, StringComparison.Ordinal)
                    && MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (media is null)
            {
                continue;
            }

            lock (job.Files)
            {
                if (!job.Files.Contains(media))
                {
                    job.Files.Add(media);
                }
            }

            FinishFile(media, job.Request.Kind, writeNfo);
        }
    }

    private static readonly string[] MediaExtensions = { ".mp4", ".mkv", ".webm", ".m4a", ".mp3", ".opus", ".ogg", ".flac", ".mov", ".avi" };

    private void FinishFile(string file, DownloadKind kind, bool writeNfo)
    {
        var basePath = Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, Path.GetFileNameWithoutExtension(file));
        var infoPath = basePath + ".info.json";
        if (!File.Exists(infoPath))
        {
            return;
        }

        try
        {
            if (writeNfo && kind != DownloadKind.Music)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = doc.RootElement;
                string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
                var date = Get("upload_date");
                var aired = date.Length == 8 ? $"{date[..4]}-{date.Substring(4, 2)}-{date.Substring(6, 2)}" : string.Empty;
                var channel = Get("channel").Length > 0 ? Get("channel") : Get("uploader");
                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
                var rootName = kind == DownloadKind.Episode ? "episodedetails" : "movie";
                sb.AppendLine("<" + rootName + ">");
                sb.AppendLine("  <title>" + SecurityElement.Escape(Get("title")) + "</title>");
                sb.AppendLine("  <plot>" + SecurityElement.Escape(Get("description")) + "</plot>");
                if (aired.Length > 0)
                {
                    sb.AppendLine((kind == DownloadKind.Episode ? "  <aired>" : "  <premiered>") + aired + (kind == DownloadKind.Episode ? "</aired>" : "</premiered>"));
                    sb.AppendLine("  <year>" + aired[..4] + "</year>");
                }

                if (channel.Length > 0)
                {
                    sb.AppendLine("  <studio>" + SecurityElement.Escape(channel) + "</studio>");
                }

                var site = Get("extractor_key");
                sb.AppendLine("  <tag>" + SecurityElement.Escape(site.Length > 0 ? site : "Downloaded") + "</tag>");

                sb.AppendLine("</" + rootName + ">");
                File.WriteAllText(basePath + ".nfo", sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the .nfo for {File}", file);
        }
        finally
        {
            try
            {
                File.Delete(infoPath);
            }
            catch (IOException)
            {
                // left for the next run
            }
        }
    }
}
