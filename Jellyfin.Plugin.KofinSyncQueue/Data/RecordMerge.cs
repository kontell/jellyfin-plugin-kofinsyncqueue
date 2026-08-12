namespace Jellyfin.Plugin.KofinSyncQueue.Data;

/// <summary>
/// Pure coalescing rules: one record per item id, whatever the event storm
/// looked like. Added+Updated stays Added (reasons OR'd), anything+Removed
/// becomes Removed, a fresh event after Removed starts over.
/// </summary>
public static class RecordMerge
{
    /// <summary>
    /// Merge an incoming event into the existing record (null = none).
    /// Returns the record to persist; its <see cref="ItemRec.Id"/> carries
    /// the existing storage id so the caller updates in place.
    /// </summary>
    /// <param name="existing">The stored record for the item, if any.</param>
    /// <param name="incoming">The captured event.</param>
    /// <returns>The merged record.</returns>
    public static ItemRec Apply(ItemRec? existing, ItemEvent incoming)
    {
        var record = new ItemRec
        {
            Id = existing?.Id ?? 0,
            ItemId = incoming.ItemId,
            MediaType = incoming.MediaType,
            ItemType = incoming.ItemType,
            LastModified = incoming.Timestamp,
            SeriesId = incoming.SeriesId ?? existing?.SeriesId,
            SeasonId = incoming.SeasonId ?? existing?.SeasonId,
            // Sticky for the same reason the parent ids are, and load-bearing
            // for removals: by the time ItemRemoved fires the parent chain can
            // already be gone, so the removal contributes null and inherits the
            // library the Added record resolved.
            LibraryIds = incoming.LibraryIds ?? existing?.LibraryIds,
        };

        if (incoming.Status == ItemStatus.Removed)
        {
            record.Status = ItemStatus.Removed;
            record.UpdateReasons = 0;
            return record;
        }

        if (existing is null || existing.Status == ItemStatus.Removed)
        {
            // Fresh, or a re-appearance after removal: start over from the
            // incoming event.
            record.Status = incoming.Status;
            record.UpdateReasons = incoming.UpdateReasons;
            return record;
        }

        // Added swallows Updated in either order; reasons accumulate.
        record.Status =
            existing.Status == ItemStatus.Added || incoming.Status == ItemStatus.Added
                ? ItemStatus.Added
                : ItemStatus.Updated;
        record.UpdateReasons = existing.UpdateReasons | incoming.UpdateReasons;

        return record;
    }
}
