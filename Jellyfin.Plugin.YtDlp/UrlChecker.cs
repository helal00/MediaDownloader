using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>The answer to "can this link be downloaded?".</summary>
public class LinkCheck
{
    /// <summary>Gets or sets the result: "supported", "generic" (no site handler, but media found on the page), "unsupported" or "error".</summary>
    public string Result { get; set; } = "error";

    /// <summary>Gets or sets yt-dlp's site handler that matched (for example "Youtube", "BBCCoUk").</summary>
    public string Site { get; set; } = string.Empty;

    /// <summary>Gets or sets "video" or "playlist".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the title of the video or playlist.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of items of a playlist or channel (0 when unknown).</summary>
    public int Items { get; set; }

    /// <summary>Gets or sets the uploader or channel.</summary>
    public string Uploader { get; set; } = string.Empty;

    /// <summary>Gets or sets the length in seconds (0 when unknown).</summary>
    public double Duration { get; set; }

    /// <summary>Gets or sets yt-dlp's message when it failed.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets yt-dlp's site handlers whose name matches the link's site, to show which kinds of link it takes.</summary>
    public List<string> Handlers { get; set; } = new();

    /// <summary>Gets or sets the subtitle languages the video offers (empty for playlists, whose videos are not read one by one).</summary>
    public List<SubtitleChoice> Subtitles { get; set; } = new();
}

/// <summary>A subtitle language a video offers.</summary>
public class SubtitleChoice
{
    /// <summary>Gets or sets yt-dlp's language code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the site's name for the language (may be empty).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the site makes them automatically (speech recognition or translation).</summary>
    public bool Auto { get; set; }
}

/// <summary>
/// Checks links with yt-dlp without downloading: which site handler takes the link, and what it finds.
/// </summary>
public class UrlChecker
{
    private static readonly string[] SecondLevel = { "co", "com", "org", "net", "ac", "gov", "edu", "ne", "or", "go" };

    private readonly YtDlpBinary _binary;
    private readonly DenoBinary _deno;
    private readonly ILogger<UrlChecker> _logger;
    private readonly SemaphoreSlim _listLock = new(1, 1);
    private List<string>? _extractors;
    private string _extractorsVersion = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="UrlChecker"/> class.
    /// </summary>
    /// <param name="binary">The yt-dlp keeper.</param>
    /// <param name="deno">The deno keeper.</param>
    /// <param name="logger">Logger.</param>
    public UrlChecker(YtDlpBinary binary, DenoBinary deno, ILogger<UrlChecker> logger)
    {
        _binary = binary;
        _deno = deno;
        _logger = logger;
    }

    /// <summary>Gets the number of site handlers of the current yt-dlp (0 before the first check).</summary>
    public int HandlerCount => _extractors?.Count ?? 0;

    /// <summary>Checks a link.</summary>
    /// <param name="url">The link.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What yt-dlp found.</returns>
    public async Task<LinkCheck> CheckAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return new LinkCheck { Result = "error", Message = "Paste a full link starting with https:// (or http://)." };
        }

        var program = await _binary.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var config = Plugin.Instance!.Configuration;
        var args = new List<string>
        {
            "--flat-playlist", "-J", "--no-warnings", "--no-colors", "--encoding", "utf-8",
            "--playlist-items", "1:200", "--socket-timeout", "20",
            "--cache-dir", Path.Combine(YtDlpBinary.DataFolder, "cache")
        };
        if (config.UseDeno && File.Exists(DenoBinary.ManagedPath))
        {
            args.Add("--js-runtimes");
            args.Add("deno:" + DenoBinary.ManagedPath);
        }

        var clients = (config.YouTubePlayerClient ?? string.Empty).Trim();
        if (clients.Length > 0)
        {
            args.Add("--extractor-args");
            args.Add("youtube:player_client=" + clients);
        }

        args.Add("--");
        args.Add(uri.ToString());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var (exit, stdout, stderr) = await RunAsync(program, args, timeout.Token).ConfigureAwait(false);

        if (exit == 0 && stdout.TrimStart().StartsWith('{'))
        {
            try
            {
                return Describe(stdout);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not read yt-dlp's answer for {Url}", uri);
            }
        }

        var error = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal))
            ?? (timeout.IsCancellationRequested ? "The site did not answer within 90 seconds." : $"yt-dlp ended with code {exit}.");
        var result = new LinkCheck
        {
            Result = error.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) ? "unsupported" : "error",
            Message = error.StartsWith("ERROR: ", StringComparison.Ordinal) ? error.Substring(7) : error
        };
        result.Handlers = await HandlersForAsync(uri, program, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static LinkCheck Describe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        var site = Get("extractor_key");
        var isPlaylist = Get("_type") == "playlist";
        var items = 0;
        if (isPlaylist)
        {
            if (root.TryGetProperty("playlist_count", out var c) && c.ValueKind == JsonValueKind.Number)
            {
                items = c.GetInt32();
            }
            else if (root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array)
            {
                items = e.GetArrayLength();
            }
        }

        double duration = 0;
        if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
        {
            duration = d.GetDouble();
        }

        var subtitles = new List<SubtitleChoice>();
        if (!isPlaylist)
        {
            AddSubtitles(root, "subtitles", false, subtitles);
            AddSubtitles(root, "automatic_captions", true, subtitles);
        }

        return new LinkCheck
        {
            Subtitles = subtitles,
            Result = string.Equals(site, "Generic", StringComparison.OrdinalIgnoreCase) ? "generic" : "supported",
            Site = site,
            Kind = isPlaylist ? "playlist" : "video",
            Title = Get("title"),
            Items = items,
            Uploader = Get("channel").Length > 0 ? Get("channel") : Get("uploader"),
            Duration = duration
        };
    }

    private static void AddSubtitles(JsonElement root, string property, bool auto, List<SubtitleChoice> list)
    {
        if (!root.TryGetProperty(property, out var subs) || subs.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var lang in subs.EnumerateObject())
        {
            // live_chat is a chat replay, not subtitles; a language with real subtitles is not listed again as automatic.
            if (lang.Name == "live_chat" || list.Any(c => string.Equals(c.Code, lang.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var name = string.Empty;
            if (lang.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var format in lang.Value.EnumerateArray())
                {
                    if (format.ValueKind == JsonValueKind.Object && format.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        name = n.GetString() ?? string.Empty;
                        break;
                    }
                }
            }

            list.Add(new SubtitleChoice { Code = lang.Name, Name = name, Auto = auto });
        }
    }

    private async Task<List<string>> HandlersForAsync(Uri uri, string program, CancellationToken cancellationToken)
    {
        var token = SiteToken(uri.Host);
        if (token.Length < 3)
        {
            return new List<string>();
        }

        var list = await GetExtractorsAsync(program, cancellationToken).ConfigureAwait(false);
        return list.Where(n => n.Contains(token, StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
    }

    /// <summary>Gets the main name of a site from its host: "www.bbc.co.uk" gives "bbc", "m.facebook.com" gives "facebook".</summary>
    /// <param name="host">Host name.</param>
    /// <returns>The name.</returns>
    public static string SiteToken(string host)
    {
        var labels = host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (labels.Count >= 2)
        {
            labels.RemoveAt(labels.Count - 1);
        }

        if (labels.Count >= 2 && SecondLevel.Contains(labels[^1]))
        {
            labels.RemoveAt(labels.Count - 1);
        }

        return labels.Count > 0 ? labels[^1] : string.Empty;
    }

    private async Task<List<string>> GetExtractorsAsync(string program, CancellationToken cancellationToken)
    {
        await _listLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = File.Exists(program) ? File.GetLastWriteTimeUtc(program).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            if (_extractors is null || version != _extractorsVersion)
            {
                var (exit, stdout, _) = await RunAsync(program, new List<string> { "--list-extractors", "--encoding", "utf-8" }, cancellationToken).ConfigureAwait(false);
                _extractors = exit == 0
                    ? stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                    : new List<string>();
                _extractorsVersion = version;
            }

            return _extractors;
        }
        finally
        {
            _listLock.Release();
        }
    }

    /// <summary>Gets the site handler count, reading the list when needed.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The count (0 when yt-dlp is not there yet).</returns>
    public async Task<int> CountHandlersAsync(CancellationToken cancellationToken)
    {
        var program = YtDlpBinary.EffectivePath;
        if (!File.Exists(program))
        {
            return 0;
        }

        return (await GetExtractorsAsync(program, cancellationToken).ConfigureAwait(false)).Count;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string program, List<string> args, CancellationToken cancellationToken)
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
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("yt-dlp did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // already ended
            }

            return (-1, string.Empty, string.Empty);
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
