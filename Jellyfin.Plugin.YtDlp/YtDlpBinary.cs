using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>
/// Keeps the plugin's own yt-dlp program: downloads it from yt-dlp's GitHub releases, checks its SHA-256
/// against the release's SHA2-256SUMS, and replaces it only when the check passes.
/// </summary>
public class YtDlpBinary
{
    private const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YtDlpBinary> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpBinary"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public YtDlpBinary(IHttpClientFactory httpClientFactory, ILogger<YtDlpBinary> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Gets the folder where the plugin keeps yt-dlp and its cache.</summary>
    public static string DataFolder
    {
        get
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not loaded");
            var dir = plugin.DataFolderPath;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Gets the release file name for this operating system and processor.</summary>
    public static string AssetName
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return RuntimeInformation.OSArchitecture == Architecture.X86 ? "yt-dlp_x86.exe" : "yt-dlp.exe";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "yt-dlp_macos";
            }

            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => "yt-dlp_linux"
            };
        }
    }

    /// <summary>Gets the path of the plugin's own copy.</summary>
    public static string ManagedPath => Path.Combine(DataFolder, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>Gets the program to run: the custom path from the settings, else the plugin's own copy.</summary>
    public static string EffectivePath
    {
        get
        {
            var custom = Plugin.Instance?.Configuration.CustomYtDlpPath?.Trim();
            return string.IsNullOrEmpty(custom) ? ManagedPath : custom;
        }
    }

    /// <summary>Makes sure a yt-dlp program exists, downloading the plugin's copy when needed.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The program's path.</returns>
    public async Task<string> EnsureAsync(CancellationToken cancellationToken)
    {
        var path = EffectivePath;
        if (File.Exists(path))
        {
            return path;
        }

        if (path != ManagedPath)
        {
            throw new FileNotFoundException("The yt-dlp program set in the settings does not exist: " + path);
        }

        await UpdateAsync(cancellationToken).ConfigureAwait(false);
        return ManagedPath;
    }

    /// <summary>Downloads the latest yt-dlp release into the plugin's folder after checking its SHA-256.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The installed version.</returns>
    public async Task<string> UpdateAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var asset = AssetName;
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-MediaDownloader-Plugin/1.3");

            var sums = await client.GetStringAsync(ReleaseBase + "SHA2-256SUMS", cancellationToken).ConfigureAwait(false);
            var expected = sums.Split('\n')
                .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length == 2 && p[1] == asset)
                .Select(p => p[0].ToLowerInvariant())
                .FirstOrDefault() ?? throw new InvalidOperationException("yt-dlp's SHA2-256SUMS has no entry for " + asset);

            var temp = ManagedPath + ".download";
            await using (var source = await client.GetStreamAsync(ReleaseBase + asset, cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            string actual;
            await using (var check = File.OpenRead(temp))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }

            if (actual != expected)
            {
                File.Delete(temp);
                throw new InvalidOperationException($"The downloaded yt-dlp does not match its published SHA-256 (expected {expected}, got {actual}); not installed.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(temp, ManagedPath, true);
            var version = await GetVersionAsync(ManagedPath, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("yt-dlp {Version} installed at {Path} (SHA-256 {Hash})", version, ManagedPath, actual);
            return version;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Gets the version of a yt-dlp program.</summary>
    /// <param name="path">Program path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The version, or an empty string when it does not run.</returns>
    public static async Task<string> GetVersionAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--version");
        using var process = Process.Start(psi);
        if (process is null)
        {
            return string.Empty;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return output.Trim();
    }
}
