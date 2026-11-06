using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public sealed class SyncStoreTests : IDisposable
{
    // Past every stamp a test writes, so a read there sees all of them.
    private const long Later = 1_000_000;

    private readonly string _directory;
    private readonly ManualClock _clock = new ManualClock();
    private readonly SyncStore _store;

    public SyncStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kofinsq-tests", Guid.NewGuid().ToString("N"));
        _store = new SyncStore(_directory, NullLogger<SyncStore>.Instance, _clock);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private void Write(long at, IEnumerable<ItemEvent> events)
    {
        _clock.Seconds = at;
        _store.RecordItemEvents(events);
    }

    private void WriteUserData(long at, Guid userId, Guid itemId, string json)
    {
        _clock.Seconds = at;
        _store.RecordUserData(userId, itemId, "movies", json);
    }

    private FeedSnapshot ReadLater(long since = 0, Guid userId = default)
    {
        _clock.Seconds = Later;
        return _store.Snapshot(since, userId);
    }

    private static ItemEvent Event(Guid id, ItemStatus status, int reasons = 0)
    {
        return new ItemEvent
        {
            ItemId = id,
            Status = status,
            MediaType = "movies",
            ItemType = "Movie",
            UpdateReasons = reasons,
        };
    }

    [Fact]
    public void CoalescesToOneRecordPerItem()
    {
        var id = Guid.NewGuid();
        Write(200, new[]
        {
            Event(id, ItemStatus.Added, reasons: 2),
            Event(id, ItemStatus.Updated, reasons: 4),
        });

        var records = ReadLater(0).Items;

        var record = Assert.Single(records);
        Assert.Equal(ItemStatus.Added, record.Status);
        Assert.Equal(6, record.UpdateReasons);
        Assert.Equal(200, record.LastModified);
    }

    [Fact]
    public void SinceIsStrictlyGreater()
    {
        var id = Guid.NewGuid();
        Write(100, new[] { Event(id, ItemStatus.Added) });

        Assert.Empty(ReadLater(100).Items);
        Assert.Single(ReadLater(99).Items);
    }

    [Fact]
    public void UserDataIsPerUserAndNewestWins()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var item = Guid.NewGuid();

        WriteUserData(100, userA, item, "{\"Played\":false}");
        WriteUserData(200, userA, item, "{\"Played\":true}");
        WriteUserData(300, userB, item, "{\"Played\":false}");

        var forA = ReadLater(0, userA).UserData;
        var record = Assert.Single(forA);
        Assert.Contains("true", record.Json, StringComparison.Ordinal);
        Assert.Equal(200, record.LastModified);

        Assert.Single(ReadLater(0, userB).UserData);
        Assert.Empty(ReadLater(0, Guid.NewGuid()).UserData);
    }

    [Fact]
    public void RetentionDeletesOldRecordsOnly()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        Write(100, new[] { Event(oldId, ItemStatus.Added) });
        Write(900, new[] { Event(newId, ItemStatus.Added) });
        WriteUserData(100, Guid.NewGuid(), oldId, "{}");

        var deleted = _store.DeleteOlderThan(500);

        Assert.Equal(2, deleted);
        var remaining = Assert.Single(ReadLater(0).Items);
        Assert.Equal(newId, remaining.ItemId);
    }

    [Fact]
    public void ReAddAfterRemoveSurvivesRoundTrip()
    {
        var id = Guid.NewGuid();
        Write(100, new[] { Event(id, ItemStatus.Added) });
        Write(200, new[] { Event(id, ItemStatus.Removed) });
        Write(300, new[] { Event(id, ItemStatus.Added) });

        var record = Assert.Single(ReadLater(0).Items);
        Assert.Equal(ItemStatus.Added, record.Status);
        Assert.Equal(300, record.LastModified);
    }

    [Fact]
    public void ParentIdsPersist()
    {
        var id = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var itemEvent = Event(id, ItemStatus.Added);
        itemEvent.ItemType = "Episode";
        itemEvent.MediaType = "tvshows";
        itemEvent.SeriesId = seriesId;

        Write(100, new[] { itemEvent });
        Write(200, new[] { Event(id, ItemStatus.Updated) });

        var record = Assert.Single(ReadLater(0).Items);
        Assert.Equal(seriesId, record.SeriesId);
    }

    [Fact]
    public void LibraryIdsRoundTripThroughLiteDb()
    {
        // LiteDB maps the POCO as-is, and a collection type it cannot
        // instantiate fails here rather than in production.
        var id = Guid.NewGuid();
        var libraries = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var itemEvent = Event(id, ItemStatus.Added);
        itemEvent.LibraryIds = libraries;

        Write(100, new[] { itemEvent });

        var record = Assert.Single(ReadLater(0).Items);
        Assert.Equal(libraries, record.LibraryIds);
    }

    [Fact]
    public void ARecordStoredBeforeTheDimensionReadsBackUnknown()
    {
        var id = Guid.NewGuid();
        Write(100, new[] { Event(id, ItemStatus.Added) });

        var record = Assert.Single(ReadLater(0).Items);
        Assert.Null(record.LibraryIds);
    }

    [Fact]
    public void BackfilledLibrariesPersist()
    {
        var id = Guid.NewGuid();
        Write(100, new[] { Event(id, ItemStatus.Added) });

        var record = Assert.Single(ReadLater(0).Items);
        record.LibraryIds = new List<Guid> { Guid.NewGuid() };
        _store.BackfillLibraries(new[] { record });

        Assert.Equal(record.LibraryIds, Assert.Single(ReadLater(0).Items).LibraryIds);
    }

    [Fact]
    public void DeadLibraryRecordsAreReapedAndTheRestAreLeftAlone()
    {
        var live = Guid.NewGuid();
        var deleted = Guid.NewGuid();

        var kept = Event(Guid.NewGuid(), ItemStatus.Added);
        kept.LibraryIds = new List<Guid> { live };

        var ghost = Event(Guid.NewGuid(), ItemStatus.Added);
        ghost.LibraryIds = new List<Guid> { deleted };

        // One foot in each: still reachable, so still wanted.
        var straddling = Event(Guid.NewGuid(), ItemStatus.Added);
        straddling.LibraryIds = new List<Guid> { deleted, live };

        var unknown = Event(Guid.NewGuid(), ItemStatus.Added);

        Write(100, new[] { kept, ghost, straddling, unknown });

        var reaped = _store.DeleteWithDeadLibraries(new HashSet<Guid> { live });

        Assert.Equal(1, reaped);
        var remaining = ReadLater(0).Items.Select(x => x.ItemId).ToHashSet();
        Assert.DoesNotContain(ghost.ItemId, remaining);
        Assert.Contains(kept.ItemId, remaining);
        Assert.Contains(straddling.ItemId, remaining);
        // Unknown is never evidence of a deleted library.
        Assert.Contains(unknown.ItemId, remaining);
    }

    [Fact]
    public void DeleteItemsDropsExactlyTheNamedRecords()
    {
        var doomed = Guid.NewGuid();
        var spared = Guid.NewGuid();
        Write(100, new[]
        {
            Event(doomed, ItemStatus.Added),
            Event(spared, ItemStatus.Added),
        });

        Assert.Equal(1, _store.DeleteItems(new[] { doomed }));
        Assert.Equal(spared, Assert.Single(ReadLater(0).Items).ItemId);
    }

    [Fact]
    public void RecordsComeBackOldestFirst()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Write(900, new[] { Event(second, ItemStatus.Added) });
        Write(100, new[] { Event(first, ItemStatus.Added) });

        var records = ReadLater(0).Items;

        Assert.Equal(new[] { first, second }, records.Select(r => r.ItemId).ToArray());
    }

    [Fact]
    public void ASnapshotNeverHandsOutACursorPastARowItDidNotServe()
    {
        var id = Guid.NewGuid();
        var user = Guid.NewGuid();
        Write(1000, new[] { Event(id, ItemStatus.Added) });
        WriteUserData(1000, user, id, "{}");

        // Read in the second the rows were stamped: they are withheld, and
        // so is the cursor, which stops one second short of them.
        var during = _store.Snapshot(0, user);
        Assert.Equal(999, during.ServerTime);
        Assert.Empty(during.Items);
        Assert.Empty(during.UserData);

        // A watermark advanced to that cursor exactly still reaches them.
        _clock.Seconds = 1001;
        var after = _store.Snapshot(during.ServerTime, user);
        Assert.Equal(1000, after.ServerTime);
        Assert.Equal(id, Assert.Single(after.Items).ItemId);
        Assert.Single(after.UserData);
    }

    [Fact]
    public void TheStandaloneCursorFollowsTheSameRule()
    {
        _clock.Seconds = 500;

        Assert.Equal(499, _store.Cursor());
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Seconds { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Seconds);
    }
}
