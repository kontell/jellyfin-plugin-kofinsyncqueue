using System;
using System.Collections.Generic;
using Jellyfin.Plugin.KofinSyncQueue.Api;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

/// <summary>
/// Etag population and the visibility rules, exercised through the
/// BaseItem-free seam the controller projects records with.
/// </summary>
public class RecordProjectionTests
{
    private static ItemRec Record(ItemStatus status, int reasons = 0)
    {
        return new ItemRec
        {
            ItemId = Guid.NewGuid(),
            Status = status,
            MediaType = "movies",
            ItemType = "Movie",
            LastModified = 1000,
            UpdateReasons = reasons,
        };
    }

    [Fact]
    public void RemovedRecordCarriesNullEtagAndNeverLoadsTheItem()
    {
        var loads = new List<Guid>();

        var item = RecordProjection.Project(
            Record(ItemStatus.Removed),
            id =>
            {
                loads.Add(id);
                return new ItemResolution(true, "should-never-be-asked");
            });

        Assert.NotNull(item);
        Assert.Null(item!.Etag);
        Assert.Equal("Removed", item.Status);
        Assert.Empty(loads);
    }

    [Theory]
    [InlineData(ItemStatus.Added)]
    [InlineData(ItemStatus.Updated)]
    public void LiveRecordsCarryTheQueryTimeEtag(ItemStatus status)
    {
        var record = Record(status);

        var item = RecordProjection.Project(record, id =>
        {
            Assert.Equal(record.ItemId, id);
            return new ItemResolution(true, "abc123");
        });

        Assert.NotNull(item);
        Assert.Equal("abc123", item!.Etag);
        Assert.Equal(status.ToString(), item.Status);
    }

    [Fact]
    public void VanishedItemDropsTheRecord()
    {
        // Deleted between event and query — the Removed record that
        // follows covers it, so this one must not reach the client.
        var item = RecordProjection.Project(
            Record(ItemStatus.Updated),
            _ => new ItemResolution(false, null));

        Assert.Null(item);
    }

    [Fact]
    public void InvisibleItemDropsTheRecord()
    {
        var item = RecordProjection.Project(
            Record(ItemStatus.Added),
            _ => new ItemResolution(false, "leaked"));

        Assert.Null(item);
    }

    [Fact]
    public void NullEtagFromAVisibleItemSurvivesAsNull()
    {
        // A visible item with no Etag is a mismatch, not a skip: the
        // client re-downloads rather than silently keeping stale state.
        var item = RecordProjection.Project(
            Record(ItemStatus.Updated),
            _ => new ItemResolution(true, null));

        Assert.NotNull(item);
        Assert.Null(item!.Etag);
    }

    [Fact]
    public void NoReasonsMeansNullNeverTheZeroEnumName()
    {
        var item = RecordProjection.Project(
            Record(ItemStatus.Updated),
            _ => new ItemResolution(true, "e"));

        Assert.NotNull(item);
        Assert.Null(item!.UpdateReason);
    }

    [Fact]
    public void AccumulatedReasonsRenderAsFlagNames()
    {
        // ImageUpdate alone is the demotion signal the client keys off;
        // OR'd with anything else it must not read as image-only.
        var imageOnly = RecordProjection.Project(
            Record(ItemStatus.Updated, (int)MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate),
            _ => new ItemResolution(true, "e"));

        var mixed = RecordProjection.Project(
            Record(
                ItemStatus.Updated,
                (int)(MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate
                    | MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit)),
            _ => new ItemResolution(true, "e"));

        Assert.Equal("ImageUpdate", imageOnly!.UpdateReason);
        Assert.Contains("ImageUpdate", mixed!.UpdateReason);
        Assert.Contains("MetadataEdit", mixed.UpdateReason);
    }

    [Fact]
    public void IdsRenderInDashlessNFormat()
    {
        var record = Record(ItemStatus.Added);
        record.SeriesId = Guid.NewGuid();
        record.SeasonId = Guid.NewGuid();

        var item = RecordProjection.Project(record, _ => new ItemResolution(true, "e"));

        Assert.NotNull(item);
        Assert.Equal(record.ItemId.ToString("N"), item!.Id);
        Assert.Equal(record.SeriesId.Value.ToString("N"), item.SeriesId);
        Assert.Equal(record.SeasonId.Value.ToString("N"), item.SeasonId);
        Assert.DoesNotContain('-', item.Id);
    }

    [Fact]
    public void AbsentParentIdsStayNull()
    {
        var item = RecordProjection.Project(
            Record(ItemStatus.Added),
            _ => new ItemResolution(true, "e"));

        Assert.NotNull(item);
        Assert.Null(item!.SeriesId);
        Assert.Null(item.SeasonId);
    }

    [Fact]
    public void LibraryIdsRenderInDashlessNFormat()
    {
        var record = Record(ItemStatus.Added);
        var library = Guid.NewGuid();
        record.LibraryIds = new List<Guid> { library };

        var item = RecordProjection.Project(record, _ => new ItemResolution(true, "e"));

        Assert.NotNull(item);
        Assert.Equal(new[] { library.ToString("N") }, item!.LibraryIds);
    }

    [Fact]
    public void UnknownLibrariesStayNullNeverAnEmptyArray()
    {
        // The serializer omits nulls and happily writes []. Absent has to
        // mean "we do not know" on the wire, so an empty answer is null.
        var fromRecord = RecordProjection.Project(
            Record(ItemStatus.Added),
            _ => new ItemResolution(true, "e"));

        var fromResolution = RecordProjection.Project(
            Record(ItemStatus.Added),
            _ => new ItemResolution(true, "e", new List<Guid>()));

        Assert.Null(fromRecord!.LibraryIds);
        Assert.Null(fromResolution!.LibraryIds);
    }

    [Fact]
    public void APreDimensionRecordLearnsItsLibrariesAtQueryTime()
    {
        var library = Guid.NewGuid();

        var item = RecordProjection.Project(
            Record(ItemStatus.Added),
            _ => new ItemResolution(true, "e", new List<Guid> { library }));

        Assert.NotNull(item);
        Assert.Equal(new[] { library.ToString("N") }, item!.LibraryIds);
    }

    [Fact]
    public void AStoredLibraryBeatsTheQueryTimeOne()
    {
        // Capture time is the authority: it is the only reading taken while
        // a removed item could still be resolved.
        var stored = Guid.NewGuid();
        var record = Record(ItemStatus.Updated);
        record.LibraryIds = new List<Guid> { stored };

        var item = RecordProjection.Project(
            record,
            _ => new ItemResolution(true, "e", new List<Guid> { Guid.NewGuid() }));

        Assert.Equal(new[] { stored.ToString("N") }, item!.LibraryIds);
    }

    [Fact]
    public void RemovedRecordsCarryTheirStoredLibrariesWithoutLoadingAnything()
    {
        var library = Guid.NewGuid();
        var record = Record(ItemStatus.Removed);
        record.LibraryIds = new List<Guid> { library };

        var item = RecordProjection.Project(
            record,
            _ => throw new InvalidOperationException("removed records never resolve"));

        Assert.NotNull(item);
        Assert.Equal(new[] { library.ToString("N") }, item!.LibraryIds);
    }
}
