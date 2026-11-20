using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>
/// Keeps the plugin's own deno, the JavaScript runtime yt-dlp needs for YouTube (without it YouTube hides most
/// formats). Downloads it from deno's GitHub releases and checks its SHA-256 against the release's .sha256sum file.
/// </summary>
public partial class DenoBinary
{
    private const string ReleaseBase = "https://github.com/denoland/deno/releases/latest/download/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DenoBinary> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DenoBinary"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public DenoBinary(IHttpClientFactory httpClientFactory, ILogger<DenoBinary> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Gets the release file for this operating system and processor, or null when deno has none.</summary>
    public static string? AssetName
    {
        get
        {
            var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            var x64 = RuntimeInformation.OSArchitecture == Architecture.X64;
            if (OperatingSystem.IsWindows() && x64)
            {
                return "deno-x86_64-pc-windows-msvc.zip";
            }

            if (OperatingSystem.IsMacOS())
            {
                return arm ? "deno-aarch64-apple-darwin.zip" : "deno-x86_64-apple-darwin.zip";
            }

            if (OperatingSystem.IsLinux() && (x64 || arm))
            {
                return arm ? "deno-aarch64-unknown-linux-gnu.zip" : "deno-x86_64-unknown-linux-gnu.zip";
            }

            return null;
        }
    }

    /// <summary>Gets the path of the plugin's own deno.</summary>
    public static string ManagedPath => Path.Combine(YtDlpBinary.DataFolder, OperatingSystem.IsWindows() ? "deno.exe" : "deno");

    /// <summary>Makes sure deno exists, downloading it when needed.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The program's path, or null when deno is not available for this system.</returns>
    public async Task<string?> EnsureAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(ManagedPath))
        {
            return ManagedPath;
        }

        if (AssetName is null)
        {
            return null;
        }

        await UpdateAsync(cancellationToken).ConfigureAwait(false);
        return ManagedPath;
    }

    /// <summary>Downloads the latest deno after checking its SHA-256.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The installed version.</returns>
    public async Task<string> UpdateAsync(CancellationToken cancellationToken)
    {
        var asset = AssetName ?? throw new PlatformNotSupportedException("deno has no build for this system");
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-MediaDownloader-Plugin/1.6");

            var sumText = await client.GetStringAsync(ReleaseBase + asset + ".sha256sum", cancellationToken).ConfigureAwait(false);
            var match = HashPattern().Match(sumText);
            if (!match.Success)
            {
                throw new InvalidOperationException("deno's checksum file has no SHA-256 for " + asset);
            }

            var expected = match.Value.ToLowerInvariant();
            var data = YtDlpBinary.DataFolder;
            var zip = Path.Combine(data, asset + ".download");
            await using (var source = await client.GetStreamAsync(ReleaseBase + asset, cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            string actual;
            await using (var check = File.OpenRead(zip))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }

            if (actual != expected)
            {
                File.Delete(zip);
                throw new InvalidOperationException($"The downloaded deno does not match its published SHA-256 (expected {expected}, got {actual}); not installed.");
            }

            var temp = ManagedPath + ".new";
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name == Path.GetFileName(ManagedPath))
                    ?? throw new InvalidOperationException("deno's zip has no " + Path.GetFileName(ManagedPath));
                entry.ExtractToFile(temp, true);
            }

            File.Delete(zip);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(temp, ManagedPath, true);
            var version = await GetVersionAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("deno {Version} installed at {Path} (zip SHA-256 {Hash})", version, ManagedPath, actual);
            return version;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Gets the installed deno's version.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The version, or an empty string when it is not installed.</returns>
    public static async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        var line = await YtDlpBinary.GetVersionAsync(ManagedPath, cancellationToken).ConfigureAwait(false);
        return line.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
    }

    [GeneratedRegex("[0-9A-Fa-f]{64}")]
    private static partial Regex HashPattern();
}
