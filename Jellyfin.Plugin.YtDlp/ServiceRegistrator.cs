using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.YtDlp;

/// <summary>
/// Registers the plugin's services.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YtDlpBinary>();
        serviceCollection.AddSingleton<DenoBinary>();
        serviceCollection.AddSingleton<DownloadQueue>();
        serviceCollection.AddSingleton<UrlChecker>();
        serviceCollection.AddSingleton<StorageService>();
        serviceCollection.AddSingleton<FolderService>();
        serviceCollection.AddTransient<IStartupFilter, MenuLinkStartupFilter>();
    }
}
