using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public sealed class SyncStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncStore _store;

    public SyncStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kofinsq-tests", Guid.NewGuid().ToString("N"));
        _store = new SyncStore(_directory, NullLogger<SyncStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static ItemEvent Event(Guid id, ItemStatus status, long ts, int reasons = 0)
    {
        return new ItemEvent
        {
            ItemId = id,
            Status = status,
            MediaType = "movies",
            ItemType = "Movie",
            UpdateReasons = reasons,
            Timestamp = ts,
        };
    }

    [Fact]
    public void CoalescesToOneRecordPerItem()
    {
        var id = Guid.NewGuid();
        _store.RecordItemEvents(new[]
        {
            Event(id, ItemStatus.Added, 100, reasons: 2),
            Event(id, ItemStatus.Updated, 200, reasons: 4),
        });

        var records = _store.ItemsSince(0);

        var record = Assert.Single(records);
        Assert.Equal(ItemStatus.Added, record.Status);
        Assert.Equal(6, record.UpdateReasons);
        Assert.Equal(200, record.LastModified);
    }

    [Fact]
    public void SinceIsStrictlyGreater()
    {
        var id = Guid.NewGuid();
        _store.RecordItemEvents(new[] { Event(id, ItemStatus.Added, 100) });

        Assert.Empty(_store.ItemsSince(100));
        Assert.Single(_store.ItemsSince(99));
    }

    [Fact]
    public void UserDataIsPerUserAndNewestWins()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var item = Guid.NewGuid();

        _store.RecordUserData(userA, item, "movies", "{\"Played\":false}", 100);
        _store.RecordUserData(userA, item, "movies", "{\"Played\":true}", 200);
        _store.RecordUserData(userB, item, "movies", "{\"Played\":false}", 300);

        var forA = _store.UserDataSince(0, userA);
        var record = Assert.Single(forA);
        Assert.Contains("true", record.Json, StringComparison.Ordinal);
        Assert.Equal(200, record.LastModified);

        Assert.Single(_store.UserDataSince(0, userB));
        Assert.Empty(_store.UserDataSince(0, Guid.NewGuid()));
    }

    [Fact]
    public void RetentionDeletesOldRecordsOnly()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        _store.RecordItemEvents(new[]
        {
            Event(oldId, ItemStatus.Added, 100),
            Event(newId, ItemStatus.Added, 900),
        });
        _store.RecordUserData(Guid.NewGuid(), oldId, "movies", "{}", 100);

        var deleted = _store.DeleteOlderThan(500);

        Assert.Equal(2, deleted);
        var remaining = Assert.Single(_store.ItemsSince(0));
        Assert.Equal(newId, remaining.ItemId);
    }

    [Fact]
    public void ReAddAfterRemoveSurvivesRoundTrip()
    {
        var id = Guid.NewGuid();
        _store.RecordItemEvents(new[] { Event(id, ItemStatus.Added, 100) });
        _store.RecordItemEvents(new[] { Event(id, ItemStatus.Removed, 200) });
        _store.RecordItemEvents(new[] { Event(id, ItemStatus.Added, 300) });

        var record = Assert.Single(_store.ItemsSince(0));
        Assert.Equal(ItemStatus.Added, record.Status);
        Assert.Equal(300, record.LastModified);
    }

    [Fact]
    public void ParentIdsPersist()
    {
        var id = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var itemEvent = Event(id, ItemStatus.Added, 100);
        itemEvent.ItemType = "Episode";
        itemEvent.MediaType = "tvshows";
        itemEvent.SeriesId = seriesId;

        _store.RecordItemEvents(new[] { itemEvent });
        _store.RecordItemEvents(new[] { Event(id, ItemStatus.Updated, 200) });

        var record = Assert.Single(_store.ItemsSince(0));
        Assert.Equal(seriesId, record.SeriesId);
    }

    [Fact]
    public void RecordsComeBackOldestFirst()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _store.RecordItemEvents(new[]
        {
            Event(second, ItemStatus.Added, 900),
            Event(first, ItemStatus.Added, 100),
        });

        var records = _store.ItemsSince(0);

        Assert.Equal(new[] { first, second }, records.Select(r => r.ItemId).ToArray());
    }
}
