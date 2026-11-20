using System;
using System.Collections.Generic;
using Jellyfin.Plugin.YtDlp.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>
/// Media Downloader: downloads videos and audio from YouTube and the other sites yt-dlp supports into Jellyfin's libraries.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's fixed id.</summary>
    public static readonly Guid PluginId = new("16118734-a017-4a44-8210-7baef431cea4");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="xmlSerializer">Jellyfin's XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Media Downloader";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Download videos and audio from YouTube and about 1,800 other sites (everything yt-dlp supports) straight into your libraries, named for Jellyfin.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "YtDlp",
                DisplayName = "Media Downloader",
                EmbeddedResourcePath = GetType().Namespace + ".Web.ytdlp.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "download"
            },
            new PluginPageInfo
            {
                Name = "YtDlpJs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.ytdlp.js"
            }
        };
    }
}
