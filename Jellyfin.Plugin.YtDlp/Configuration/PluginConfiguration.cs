using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YtDlp.Configuration;

/// <summary>
/// Settings of the Media Downloader plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a yt-dlp program to use instead of the plugin's own copy (empty: the plugin's own copy).</summary>
    public string CustomYtDlpPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the plugin updates its own yt-dlp copy every week.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Gets or sets the default highest video height (0: best available).</summary>
    public int DefaultMaxHeight { get; set; } = 1080;

    /// <summary>Gets or sets a value indicating whether H.264 video in MP4 is preferred, which most TVs play without transcoding.</summary>
    public bool PreferTvCompatible { get; set; } = true;

    /// <summary>Gets or sets the subtitle languages to fetch, comma separated (empty: none).</summary>
    public string SubtitleLanguages { get; set; } = "en";

    /// <summary>Gets or sets a value indicating whether an .nfo file with title, description and date is written.</summary>
    public bool WriteNfo { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the library is scanned after each finished download.</summary>
    public bool ScanAfterDownload { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the plugin's own deno (JavaScript runtime) is used; YouTube hides most formats without one.</summary>
    public bool UseDeno { get; set; } = true;

    /// <summary>Gets or sets the YouTube player types yt-dlp asks as (youtube:player_client); empty: yt-dlp's default.</summary>
    public string YouTubePlayerClient { get; set; } = "default,web_safari";

    /// <summary>Gets or sets extra yt-dlp arguments, one per line (for advanced use).</summary>
    public string ExtraArguments { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Jellyfin's side menu shows a link to the downloader page.</summary>
    public bool ShowMenuLink { get; set; } = true;

    /// <summary>Gets or sets the menu link's name.</summary>
    public string MenuLinkName { get; set; } = "Media Downloader";

    /// <summary>Gets or sets the menu link's icon (a Material icon name).</summary>
    public string MenuLinkIcon { get; set; } = "download";

    /// <summary>Gets or sets the users who are not administrators but may use the downloader page, each limited to their own folders.</summary>
    public List<UserAccess> UserAccess { get; set; } = new();
}

/// <summary>A user who may use the downloader page, and where.</summary>
public class UserAccess
{
    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's name when it was saved (for display only).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the folders the user may download into and see in My files (each inside a library folder).</summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the user may delete files inside their folders.</summary>
    public bool CanDelete { get; set; } = true;
}
