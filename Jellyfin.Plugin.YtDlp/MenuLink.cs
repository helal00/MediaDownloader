using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>
/// Adds the Media Downloader link to the web client's side menu.
/// Jellyfin's web client reads its menu links from web/config.json; the plugin adds its link to that
/// file as it is sent, so nothing on disk changes and the link survives Jellyfin updates.
/// </summary>
public class MenuLinkStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<MenuLinkMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Rewrites web/config.json to add the Media Downloader menu link.
/// </summary>
public class MenuLinkMiddleware
{
    private const int MaxConfigBytes = 1024 * 1024;
    private readonly RequestDelegate _next;
    private readonly ILogger<MenuLinkMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MenuLinkMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="logger">Logger.</param>
    public MenuLinkMiddleware(RequestDelegate next, ILogger<MenuLinkMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var config = Plugin.Instance?.Configuration;
        var path = context.Request.Path.Value ?? string.Empty;
        if (config is null
            || !config.ShowMenuLink
            || !HttpMethods.IsGet(context.Request.Method)
            || !path.EndsWith("/web/config.json", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for the plain file: no compression, and no "not modified" answer that would keep the browser's old copy.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        var bytes = buffer.ToArray();
        if (context.Response.StatusCode == StatusCodes.Status200OK && bytes.Length > 0 && bytes.Length < MaxConfigBytes)
        {
            try
            {
                bytes = AddLink(bytes, config.MenuLinkName, config.MenuLinkIcon);
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = bytes.Length;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Media Downloader could not add its menu link to web/config.json; the file is sent unchanged.");
            }
        }

        await original.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the link to a web config.
    /// </summary>
    /// <param name="json">The config as sent by Jellyfin.</param>
    /// <param name="name">The link's name.</param>
    /// <param name="icon">The link's Material icon name.</param>
    /// <returns>The changed config.</returns>
    public static byte[] AddLink(byte[] json, string? name, string? icon)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            throw new InvalidOperationException("web/config.json is not a JSON object.");
        }

        if (root["menuLinks"] is not JsonArray links)
        {
            links = new JsonArray();
            root["menuLinks"] = links;
        }

        // The web client runs at <base>/web/, so this reaches <base>/MediaDownloader with any base URL.
        const string Url = "../MediaDownloader";
        var exists = links.OfType<JsonObject>().Any(l => string.Equals((string?)l["url"], Url, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            links.Add(new JsonObject
            {
                ["name"] = string.IsNullOrWhiteSpace(name) ? "Media Downloader" : name.Trim(),
                ["icon"] = string.IsNullOrWhiteSpace(icon) ? "download" : icon.Trim(),
                ["url"] = Url
            });
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
