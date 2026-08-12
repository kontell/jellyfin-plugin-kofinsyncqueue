using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue.Data;

/// <summary>
/// The LiteDB store, used correctly: indexed lookups and single-record
/// writes — never a FindAll rewrite (the official plugin's O(n²) path,
/// DbRepo.cs:119-138, was a usage bug, not a LiteDB limit).
/// </summary>
public class SyncStore : IDisposable
{
    private const string ItemsCollection = "items";
    private const string UserDataCollection = "userdata";

    private readonly LiteDatabase _db;
    private readonly ILogger<SyncStore> _logger;
    private readonly object _writeLock = new object();

    public SyncStore(string dataPath, ILogger<SyncStore> logger)
    {
        _logger = logger;
        var directory = Path.Combine(dataPath, "kofinsyncqueue");
        Directory.CreateDirectory(directory);
        _db = new LiteDatabase($"filename={Path.Combine(directory, "kofinsyncqueue.db")}");
        _db.UserVersion = 1;

        Items.EnsureIndex(x => x.ItemId, unique: true);
        Items.EnsureIndex(x => x.LastModified);
        UserData.EnsureIndex(x => x.ItemId);
        UserData.EnsureIndex(x => x.LastModified);

        _logger.LogInformation("Kofin sync store open at {Directory}", directory);
    }

    private ILiteCollection<ItemRec> Items => _db.GetCollection<ItemRec>(ItemsCollection);

    private ILiteCollection<UserDataRec> UserData => _db.GetCollection<UserDataRec>(UserDataCollection);

    /// <summary>
    /// Record a batch of item events, coalescing per item id on write.
    /// </summary>
    /// <param name="events">The captured events, in occurrence order.</param>
    public void RecordItemEvents(IEnumerable<ItemEvent> events)
    {
        lock (_writeLock)
        {
            foreach (var itemEvent in events)
            {
                var id = itemEvent.ItemId;
                var existing = Items.FindOne(x => x.ItemId == id);
                var merged = RecordMerge.Apply(existing, itemEvent);

                if (existing is null)
                {
                    Items.Insert(merged);
                }
                else
                {
                    Items.Update(merged);
                }
            }
        }
    }

    /// <summary>
    /// Record one user-data change: single row per (user, item), newest wins.
    /// </summary>
    public void RecordUserData(Guid userId, Guid itemId, string mediaType, string json, long timestamp)
    {
        lock (_writeLock)
        {
            var existing = UserData.FindOne(x => x.ItemId == itemId && x.UserId == userId);
            var record = new UserDataRec
            {
                Id = existing?.Id ?? 0,
                UserId = userId,
                ItemId = itemId,
                MediaType = mediaType,
                Json = json,
                LastModified = timestamp,
            };

            if (existing is null)
            {
                UserData.Insert(record);
            }
            else
            {
                UserData.Update(record);
            }
        }
    }

    /// <summary>
    /// Change records strictly after the watermark, oldest first.
    /// </summary>
    public IReadOnlyList<ItemRec> ItemsSince(long since)
    {
        return Items.Find(x => x.LastModified > since)
            .OrderBy(x => x.LastModified)
            .ToList();
    }

    /// <summary>
    /// The caller's user-data changes strictly after the watermark.
    /// </summary>
    public IReadOnlyList<UserDataRec> UserDataSince(long since, Guid userId)
    {
        return UserData.Find(x => x.LastModified > since && x.UserId == userId)
            .OrderBy(x => x.LastModified)
            .ToList();
    }

    /// <summary>
    /// Persist libraries learned at query time for records stored before the
    /// dimension existed. Single-record updates, as everywhere else here.
    /// </summary>
    /// <param name="records">Records whose <see cref="ItemRec.LibraryIds"/> was just resolved.</param>
    public void BackfillLibraries(IReadOnlyCollection<ItemRec> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

        var filled = 0;

        lock (_writeLock)
        {
            foreach (var record in records)
            {
                var id = record.ItemId;
                var current = Items.FindOne(x => x.ItemId == id);

                // Re-read and fill only the one field. The record was read
                // outside the lock, and a recorder flush in between would be
                // reverted by writing the whole thing back — a Removed record
                // quietly becoming Added again is not worth the shortcut.
                if (current is null || current.LibraryIds is not null)
                {
                    continue;
                }

                current.LibraryIds = record.LibraryIds;
                Items.Update(current);
                filled++;
            }
        }

        if (filled > 0)
        {
            _logger.LogInformation("Learned the library of {Count} older records", filled);
        }
    }

    /// <summary>
    /// Drop item records by Jellyfin item id.
    /// </summary>
    /// <param name="itemIds">The items whose records should go.</param>
    /// <returns>How many records were deleted.</returns>
    public int DeleteItems(IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        if (itemIds.Count == 0)
        {
            return 0;
        }

        lock (_writeLock)
        {
            var deleted = 0;

            foreach (var itemId in itemIds)
            {
                var id = itemId;
                deleted += Items.DeleteMany(x => x.ItemId == id);
            }

            return deleted;
        }
    }

    /// <summary>
    /// Drop records whose every library is gone — what a deleted Jellyfin
    /// library leaves behind, since removing one deletes the .mblink
    /// directory and fires no per-item event.
    /// </summary>
    /// <remarks>
    /// This walks the records that carry libraries, which the class comment
    /// above rules out for the request path. It is not the request path: this
    /// runs once a day from the retention task, and "disjoint from this set"
    /// has no index to answer it.
    /// </remarks>
    /// <param name="live">The collection folders that still exist.</param>
    /// <returns>How many records were deleted.</returns>
    public int DeleteWithDeadLibraries(IReadOnlySet<Guid> live)
    {
        ArgumentNullException.ThrowIfNull(live);

        lock (_writeLock)
        {
            var dead = Items.Find(x => x.LibraryIds != null)
                .Where(record => record.LibraryIds is { Count: > 0 }
                    && !record.LibraryIds.Any(live.Contains))
                .Select(record => record.Id)
                .ToList();

            if (dead.Count == 0)
            {
                return 0;
            }

            var deleted = Items.DeleteMany(x => dead.Contains(x.Id));
            _logger.LogInformation("Removed {Count} records belonging to deleted libraries", deleted);
            return deleted;
        }
    }

    /// <summary>
    /// Drop records older than the cutoff (the retention task).
    /// </summary>
    /// <returns>How many records were deleted.</returns>
    public int DeleteOlderThan(long cutoff)
    {
        lock (_writeLock)
        {
            var items = Items.DeleteMany(x => x.LastModified < cutoff);
            var userData = UserData.DeleteMany(x => x.LastModified < cutoff);
            _logger.LogInformation(
                "Retention removed {Items} item records and {UserData} user-data records",
                items,
                userData);
            return items + userData;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _db.Dispose();
        }
    }
}
