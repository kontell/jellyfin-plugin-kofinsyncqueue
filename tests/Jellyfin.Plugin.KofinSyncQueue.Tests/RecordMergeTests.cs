using System;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public class RecordMergeTests
{
    private static readonly Guid ItemId = Guid.NewGuid();

    private static ItemEvent Event(ItemStatus status, int reasons = 0, long ts = 100)
    {
        return new ItemEvent
        {
            ItemId = ItemId,
            Status = status,
            MediaType = "movies",
            ItemType = "Movie",
            UpdateReasons = reasons,
            Timestamp = ts,
        };
    }

    [Fact]
    public void FreshEventCreatesRecord()
    {
        var record = RecordMerge.Apply(null, Event(ItemStatus.Added, reasons: 2));

        Assert.Equal(ItemStatus.Added, record.Status);
        Assert.Equal(2, record.UpdateReasons);
        Assert.Equal(0, record.Id);
    }

    [Fact]
    public void AddedSwallowsUpdatedAndAccumulatesReasons()
    {
        var added = RecordMerge.Apply(null, Event(ItemStatus.Added, reasons: 2, ts: 100));
        added.Id = 7;

        var merged = RecordMerge.Apply(added, Event(ItemStatus.Updated, reasons: 4, ts: 200));

        Assert.Equal(ItemStatus.Added, merged.Status);
        Assert.Equal(6, merged.UpdateReasons);
        Assert.Equal(200, merged.LastModified);
        Assert.Equal(7, merged.Id); // updates in place
    }

    [Fact]
    public void UpdatedPlusUpdatedOrsReasons()
    {
        var first = RecordMerge.Apply(null, Event(ItemStatus.Updated, reasons: 4));
        var merged = RecordMerge.Apply(first, Event(ItemStatus.Updated, reasons: 16, ts: 150));

        Assert.Equal(ItemStatus.Updated, merged.Status);
        Assert.Equal(20, merged.UpdateReasons);
    }

    [Fact]
    public void RemovedWinsAndClearsReasons()
    {
        var added = RecordMerge.Apply(null, Event(ItemStatus.Added, reasons: 2));
        var merged = RecordMerge.Apply(added, Event(ItemStatus.Removed, ts: 300));

        Assert.Equal(ItemStatus.Removed, merged.Status);
        Assert.Equal(0, merged.UpdateReasons);
        Assert.Equal(300, merged.LastModified);
    }

    [Fact]
    public void ReAddAfterRemovedStartsOver()
    {
        var removed = RecordMerge.Apply(null, Event(ItemStatus.Removed));
        var merged = RecordMerge.Apply(removed, Event(ItemStatus.Added, reasons: 2, ts: 400));

        Assert.Equal(ItemStatus.Added, merged.Status);
        Assert.Equal(2, merged.UpdateReasons);
    }

    [Fact]
    public void ParentIdsSurviveEventsThatOmitThem()
    {
        var seriesId = Guid.NewGuid();
        var withParent = Event(ItemStatus.Added);
        withParent.SeriesId = seriesId;

        var first = RecordMerge.Apply(null, withParent);
        var merged = RecordMerge.Apply(first, Event(ItemStatus.Updated, ts: 500));

        Assert.Equal(seriesId, merged.SeriesId);
    }
}
