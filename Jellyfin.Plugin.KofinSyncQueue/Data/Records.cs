using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.KofinSyncQueue.Data;

/// <summary>
/// Change-record status. An item has exactly one record at a time
/// (coalesced on write, see <see cref="RecordMerge"/>).
/// </summary>
public enum ItemStatus
{
    Added,
    Updated,
    Removed,
}

/// <summary>
/// The stored change record. No Etag here — Etags are computed at query
/// time from the live item, which coalesces an event storm on one item
/// into a single current-state comparison.
/// </summary>
public class ItemRec
{
    /// <summary>Gets or sets the LiteDB id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the coalesced status.</summary>
    public ItemStatus Status { get; set; }

    /// <summary>Gets or sets the media-type class (movies|tvshows|boxsets|musicvideos|music).</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind (Movie|Series|Season|Episode|...).</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the record write time, unix seconds.</summary>
    public long LastModified { get; set; }

    /// <summary>Gets or sets the accumulated ItemUpdateType bits (None masked out).</summary>
    public int UpdateReasons { get; set; }

    /// <summary>Gets or sets the series id for episodes/seasons.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the season id for episodes.</summary>
    public Guid? SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the collection folders the item belongs to — the ids
    /// clients whitelist. Null means unknown (a pre-v1.1 record, or an item
    /// whose parent chain was already gone when the event fired); it never
    /// means "belongs to nothing", which is why an empty result is stored
    /// as null. A list because a path under two libraries is in both.
    /// </summary>
    public List<Guid>? LibraryIds { get; set; }
}

/// <summary>
/// Stored user-data change: the full UserItemDataDto JSON per user, the
/// good part of the old design, kept.
/// </summary>
public class UserDataRec
{
    /// <summary>Gets or sets the LiteDB id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the media-type class.</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the serialized UserItemDataDto.</summary>
    public string Json { get; set; } = string.Empty;

    /// <summary>Gets or sets the record write time, unix seconds.</summary>
    public long LastModified { get; set; }
}

/// <summary>
/// One captured library event, the recorder → store hand-off shape.
/// BaseItem-free so the store and merge logic stay unit-testable.
/// </summary>
public class ItemEvent
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the event status.</summary>
    public ItemStatus Status { get; set; }

    /// <summary>Gets or sets the media-type class.</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind.</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the ItemUpdateType bits (None masked out).</summary>
    public int UpdateReasons { get; set; }

    /// <summary>Gets or sets the series id for episodes/seasons.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the season id for episodes.</summary>
    public Guid? SeasonId { get; set; }

    /// <summary>Gets or sets the collection folders the item belongs to; null when unresolvable.</summary>
    public List<Guid>? LibraryIds { get; set; }

    /// <summary>Gets or sets the event capture time, unix seconds.</summary>
    public long Timestamp { get; set; }
}
