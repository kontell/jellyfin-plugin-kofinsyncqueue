using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.KofinSyncQueue.Api;

/// <summary>
/// GET /Kofin/SyncQueue/Info — the probe: protocol version, server clock
/// and retention in one round trip.
/// </summary>
public class SyncInfoResponse
{
    /// <summary>Gets or sets the plugin assembly version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the protocol version (clients require an exact match).</summary>
    public int ProtocolVersion { get; set; } = 1;

    /// <summary>Gets or sets the server clock, unix seconds.</summary>
    public long ServerTime { get; set; }

    /// <summary>Gets or sets the retention cutoff, unix seconds (0 = none).</summary>
    public long RetentionCutoff { get; set; }

    /// <summary>Gets or sets the configured retention days (0 = keep forever).</summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// Gets or sets the optional capabilities this server has on top of the
    /// protocol version. Additive by design: clients require an exact
    /// <see cref="ProtocolVersion"/> match, so a bump would demote every
    /// deployed client to the legacy plugin. Features are opted into by
    /// presence instead, and an old client simply ignores the list.
    /// </summary>
    public IReadOnlyList<string> Features { get; set; } = new[] { "library-scope", "playlists" };
}

/// <summary>
/// One change record in the response, Etag computed at query time.
/// </summary>
public class SyncQueueItem
{
    /// <summary>Gets or sets the item id ("N" format).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the status (Added|Updated|Removed).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the media-type class.</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind.</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the record time, unix seconds.</summary>
    public long LastModified { get; set; }

    /// <summary>Gets or sets the accumulated change reasons ("ImageUpdate, MetadataEdit"), null when unknown.</summary>
    public string? UpdateReason { get; set; }

    /// <summary>Gets or sets the current server Etag for the item; null on removed records.</summary>
    public string? Etag { get; set; }

    /// <summary>Gets or sets the series id for episodes/seasons ("N" format).</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets the season id for episodes ("N" format).</summary>
    public string? SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the collection folders the item belongs to ("N" format) —
    /// the ids clients whitelist. Absent means unknown, never "belongs to
    /// nothing": drop a record only when this is present and matches nothing.
    /// </summary>
    public IReadOnlyList<string>? LibraryIds { get; set; }
}

/// <summary>
/// GET /Kofin/SyncQueue — the change set since the watermark.
/// </summary>
public class SyncQueueResponse
{
    /// <summary>Gets or sets the server clock at query time, unix seconds (the client's next watermark).</summary>
    public long ServerTime { get; set; }

    /// <summary>Gets or sets the retention cutoff, unix seconds (0 = none). A watermark before this means records were lost.</summary>
    public long RetentionCutoff { get; set; }

    /// <summary>Gets the coalesced change records (one per item), oldest first.</summary>
    public List<SyncQueueItem> Items { get; } = new List<SyncQueueItem>();

    /// <summary>Gets the caller's user-data changes as full DTOs.</summary>
    public List<UserItemDataDto> UserData { get; } = new List<UserItemDataDto>();
}
