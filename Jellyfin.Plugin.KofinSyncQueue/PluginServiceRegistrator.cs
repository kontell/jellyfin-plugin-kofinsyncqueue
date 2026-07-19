using Jellyfin.Plugin.KofinSyncQueue.Data;
using Jellyfin.Plugin.KofinSyncQueue.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue;

/// <summary>
/// DI registration: the store is a singleton service (no Instance-pattern
/// reach-around), the two recorders are hosted services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(serviceProvider => new SyncStore(
            serviceProvider.GetRequiredService<IApplicationPaths>().DataPath,
            serviceProvider.GetRequiredService<ILogger<SyncStore>>()));
        serviceCollection.AddHostedService<LibraryChangeRecorder>();
        serviceCollection.AddHostedService<UserDataRecorder>();
    }
}
