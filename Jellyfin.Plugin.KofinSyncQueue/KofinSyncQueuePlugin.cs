using System;
using System.Collections.Generic;
using Jellyfin.Plugin.KofinSyncQueue.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.KofinSyncQueue;

/// <summary>
/// The Kofin Sync Queue plugin: a typed change queue for the Kofin Kodi
/// client (plugin.video.kofin). Protocol v1 lives under /Kofin/SyncQueue.
/// </summary>
public class KofinSyncQueuePlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public KofinSyncQueuePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the plugin instance (configuration access; services come from DI).
    /// </summary>
    public static KofinSyncQueuePlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => new Guid("eed3be84-d5cc-4f3b-b346-ba80c81cbec9");

    /// <inheritdoc />
    public override string Name => "Kofin Sync Queue";

    /// <inheritdoc />
    public override string Description
        => "Typed change queue for the Kofin Kodi client: change reasons, series/season parents, query-time Etags and an in-band retention cutoff.";

    /// <summary>
    /// Gets the configured retention in days (0 = keep forever).
    /// </summary>
    public static int RetentionDays => Instance?.Configuration.RetentionDays ?? 0;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "kofinsyncqueue",
                EmbeddedResourcePath = GetType().Namespace + ".Web.config.html",
            },
        };
    }
}
