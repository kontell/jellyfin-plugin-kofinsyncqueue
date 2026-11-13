using Jellyfin.Plugin.KofinSyncQueue.Data;
using Jellyfin.Plugin.KofinSyncQueue.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue;

/// <summary>
/// DI registration: the store and the change pusher are singleton services
/// (no Instance-pattern reach-around), the two recorders are hosted services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(serviceProvider => new SyncStore(
            serviceProvider.GetRequiredService<IApplicationPaths>().DataPath,
            serviceProvider.GetRequiredService<ILogger<SyncStore>>()));
        serviceCollection.AddSingleton(serviceProvider => new ChangePusher(
            serviceProvider.GetRequiredService<ISessionManager>(),
            serviceProvider.GetRequiredService<IUserManager>(),
            serviceProvider.GetRequiredService<ILibraryManager>(),
            serviceProvider.GetRequiredService<ILogger<ChangePusher>>()));
        serviceCollection.AddHostedService<LibraryChangeRecorder>();
        serviceCollection.AddHostedService<UserDataRecorder>();
    }
}
