using System;
using System.Collections.Generic;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public class RecordMergeTests
{
    private static readonly Guid ItemId = Guid.NewGuid();

    private static ItemEvent Event(ItemStatus status, int reasons = 0)
    {
        return new ItemEvent
        {
            ItemId = ItemId,
            Status = status,
            MediaType = "movies",
            ItemType = "Movie",
            UpdateReasons = reasons,
        };
    }

    [Fact]
    public void FreshEventCreatesRecord()
    {
        var record = RecordMerge.Apply(null, Event(ItemStatus.Added, reasons: 2), 100);

        Assert.Equal(ItemStatus.Added, record.Status);
        Assert.Equal(2, record.UpdateReasons);
        Assert.Equal(0, record.Id);
    }

    [Fact]
    public void AddedSwallowsUpdatedAndAccumulatesReasons()
    {
        var added = RecordMerge.Apply(null, Event(ItemStatus.Added, reasons: 2), 100);
        added.Id = 7;

        var merged = RecordMerge.Apply(added, Event(ItemStatus.Updated, reasons: 4), 200);

        Assert.Equal(ItemStatus.Added, merged.Status);
        Assert.Equal(6, merged.UpdateReasons);
        Assert.Equal(200, merged.LastModified);
        Assert.Equal(7, merged.Id); // updates in place
    }

    [Fact]
    public void UpdatedPlusUpdatedOrsReasons()
    {
        var first = RecordMerge.Apply(null, Event(ItemStatus.Updated, reasons: 4), 100);
        var merged = RecordMerge.Apply(first, Event(ItemStatus.Updated, reasons: 16), 150);

        Assert.Equal(ItemStatus.Updated, merged.Status);
        Assert.Equal(20, merged.UpdateReasons);
    }

    [Fact]
    public void RemovedWinsAndClearsReasons()
    {
        var added = RecordMerge.Apply(null, Event(ItemStatus.Added, reasons: 2), 100);
        var merged = RecordMerge.Apply(added, Event(ItemStatus.Removed), 300);

        Assert.Equal(ItemStatus.Removed, merged.Status);
        Assert.Equal(0, merged.UpdateReasons);
        Assert.Equal(300, merged.LastModified);
    }

    [Fact]
    public void ReAddAfterRemovedStartsOver()
    {
        var removed = RecordMerge.Apply(null, Event(ItemStatus.Removed), 100);
        var merged = RecordMerge.Apply(removed, Event(ItemStatus.Added, reasons: 2), 400);

        Assert.Equal(ItemStatus.Added, merged.Status);
        Assert.Equal(2, merged.UpdateReasons);
    }

    [Fact]
    public void ParentIdsSurviveEventsThatOmitThem()
    {
        var seriesId = Guid.NewGuid();
        var withParent = Event(ItemStatus.Added);
        withParent.SeriesId = seriesId;

        var first = RecordMerge.Apply(null, withParent, 100);
        var merged = RecordMerge.Apply(first, Event(ItemStatus.Updated), 500);

        Assert.Equal(seriesId, merged.SeriesId);
    }

    [Fact]
    public void LibraryIdsSurviveEventsThatOmitThem()
    {
        var library = Guid.NewGuid();
        var withLibrary = Event(ItemStatus.Added);
        withLibrary.LibraryIds = new List<Guid> { library };

        var first = RecordMerge.Apply(null, withLibrary, 100);
        var merged = RecordMerge.Apply(first, Event(ItemStatus.Updated), 500);

        Assert.Equal(new[] { library }, merged.LibraryIds);
    }

    [Fact]
    public void RemovedInheritsTheLibraryItCanNoLongerResolve()
    {
        // The load-bearing case: DeleteItem clears the item's parent before
        // the event fires, so a removal can arrive knowing nothing. It must
        // still be attributable, or clients cannot scope removals at all.
        var library = Guid.NewGuid();
        var added = Event(ItemStatus.Added);
        added.LibraryIds = new List<Guid> { library };

        var first = RecordMerge.Apply(null, added, 100);
        var merged = RecordMerge.Apply(first, Event(ItemStatus.Removed), 900);

        Assert.Equal(ItemStatus.Removed, merged.Status);
        Assert.Equal(new[] { library }, merged.LibraryIds);
    }

    [Fact]
    public void ARehomedItemTakesItsNewLibrary()
    {
        var before = Guid.NewGuid();
        var after = Guid.NewGuid();

        var first = Event(ItemStatus.Added);
        first.LibraryIds = new List<Guid> { before };

        var second = Event(ItemStatus.Updated);
        second.LibraryIds = new List<Guid> { after };

        var merged = RecordMerge.Apply(RecordMerge.Apply(null, first, 100), second, 600);

        Assert.Equal(new[] { after }, merged.LibraryIds);
    }
}
