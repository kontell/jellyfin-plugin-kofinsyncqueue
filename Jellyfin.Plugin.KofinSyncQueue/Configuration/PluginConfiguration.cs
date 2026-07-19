using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.KofinSyncQueue.Configuration;

/// <summary>
/// Plugin configuration: retention only. The queue is always on while the
/// plugin is installed — there is deliberately no enable toggle and no
/// per-media-type tracking switches (clients filter by type at query time).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets how many days of change records to keep. 0 keeps them
    /// forever (the response envelope then reports no retention cutoff).
    /// Default 90 — never unbounded growth out of the box.
    /// </summary>
    public int RetentionDays { get; set; } = 90;
}
