using System;
using System.Globalization;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.KofinSyncQueue.Api;

/// <summary>
/// What resolving a record's live item told us: whether it is still there
/// and visible to the caller, and its query-time Etag.
/// </summary>
/// <param name="Visible">Whether the item exists and the caller may see it.</param>
/// <param name="Etag">The item's current Etag; ignored when not visible.</param>
public readonly record struct ItemResolution(bool Visible, string? Etag);

/// <summary>
/// Turns a stored record into a response item. BaseItem-free — the live
/// lookup arrives as a delegate — so the Etag and visibility rules stay
/// unit-testable (same seam as <see cref="ItemEvent"/> on the write side).
/// </summary>
public static class RecordProjection
{
    /// <summary>
    /// Projects one record, or null when the record should be dropped
    /// from the response (item gone, or not visible to the caller).
    /// </summary>
    /// <param name="record">The stored change record.</param>
    /// <param name="resolve">Resolves the live item; never called for removed records.</param>
    /// <returns>The response item, or null to skip this record.</returns>
    public static SyncQueueItem? Project(ItemRec record, Func<Guid, ItemResolution> resolve)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(resolve);

        string? etag = null;

        if (record.Status != ItemStatus.Removed)
        {
            // A removed record has no item to load and carries no Etag —
            // the one case that must never touch the library.
            var resolution = resolve(record.ItemId);

            if (!resolution.Visible)
            {
                return null;
            }

            etag = resolution.Etag;
        }

        return new SyncQueueItem
        {
            Id = record.ItemId.ToString("N", CultureInfo.InvariantCulture),
            Status = record.Status.ToString(),
            MediaType = record.MediaType,
            ItemType = record.ItemType,
            LastModified = record.LastModified,
            UpdateReason = record.UpdateReasons == 0
                ? null
                : ((ItemUpdateType)record.UpdateReasons).ToString(),
            Etag = etag,
            SeriesId = record.SeriesId?.ToString("N", CultureInfo.InvariantCulture),
            SeasonId = record.SeasonId?.ToString("N", CultureInfo.InvariantCulture),
        };
    }
}
