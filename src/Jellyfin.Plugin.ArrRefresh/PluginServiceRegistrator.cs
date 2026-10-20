using Jellyfin.Plugin.ArrRefresh.Library;
using Jellyfin.Plugin.ArrRefresh.Search;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ArrRefresh;

/// <summary>
/// Registers the plugin's services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singleton: its gate serialises every webhook across requests.
        serviceCollection.AddSingleton<TargetedUpdater>();

        // One instance serves both roles: the updater tracks items on it, the host starts its event worker.
        serviceCollection.AddSingleton<SearchIndexSync>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SearchIndexSync>());
    }
}
