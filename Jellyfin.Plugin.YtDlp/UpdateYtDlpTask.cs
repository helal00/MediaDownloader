using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>
/// Weekly task: updates the plugin's own yt-dlp copy (YouTube changes often; old copies stop working).
/// </summary>
public class UpdateYtDlpTask : IScheduledTask
{
    private readonly YtDlpBinary _binary;
    private readonly DenoBinary _deno;
    private readonly ILogger<UpdateYtDlpTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateYtDlpTask"/> class.
    /// </summary>
    /// <param name="binary">The yt-dlp program keeper.</param>
    /// <param name="deno">The deno keeper.</param>
    /// <param name="logger">Logger.</param>
    public UpdateYtDlpTask(YtDlpBinary binary, DenoBinary deno, ILogger<UpdateYtDlpTask> logger)
    {
        _binary = binary;
        _deno = deno;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update yt-dlp and deno";

    /// <inheritdoc />
    public string Key => "YtDlpUpdate";

    /// <inheritdoc />
    public string Description => "Downloads the latest yt-dlp and deno for the Media Downloader plugin, after checking their SHA-256.";

    /// <inheritdoc />
    public string Category => "Media Downloader";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoUpdate)
        {
            _logger.LogInformation("yt-dlp auto update is off; skipped");
            progress.Report(100);
            return;
        }

        if (YtDlpBinary.EffectivePath == YtDlpBinary.ManagedPath)
        {
            await _binary.UpdateAsync(cancellationToken).ConfigureAwait(false);
        }

        progress.Report(50);
        if (config.UseDeno && DenoBinary.AssetName is not null)
        {
            await _deno.UpdateAsync(cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
