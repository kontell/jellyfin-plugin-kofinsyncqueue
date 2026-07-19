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
