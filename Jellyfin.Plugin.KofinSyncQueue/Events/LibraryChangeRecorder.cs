using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue.Events;

/// <summary>
/// Captures ItemAdded/ItemUpdated/ItemRemoved into typed events, batched a
/// few seconds so scan storms write once per item, bounded so the pending
/// list can never grow without limit.
/// </summary>
public class LibraryChangeRecorder : IHostedService, IDisposable
{
    private const int BatchDelayMs = 5000;
    private const int FlushEarlyAt = 5000;

    private readonly ILibraryManager _libraryManager;
    private readonly SyncStore _store;
    private readonly ILogger<LibraryChangeRecorder> _logger;
    private readonly object _lock = new object();
    private readonly List<ItemEvent> _pending = new List<ItemEvent>();

    // Library resolution memo, keyed on the item's parent. Siblings share a
    // parent, hence a library, so a scan storm pays one resolution per folder
    // instead of one per item. Lives only as long as the batch it serves.
    private readonly ConcurrentDictionary<Guid, List<Guid>?> _libraryMemo
        = new ConcurrentDictionary<Guid, List<Guid>?>();

    private Timer? _timer;

    public LibraryChangeRecorder(
        ILibraryManager libraryManager,
        SyncStore store,
        ILogger<LibraryChangeRecorder> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;

        // Subscription is otherwise invisible: a recorder that never fires
        // and a recorder that never started look identical in the log.
        _logger.LogInformation("Kofin library change recorder subscribed");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        Flush(null);
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        => Capture(e, ItemStatus.Added);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        => Capture(e, ItemStatus.Updated);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        => Capture(e, ItemStatus.Removed);

    private void Capture(ItemChangeEventArgs e, ItemStatus status)
    {
        // Live phase 5: Added and Removed reach this method but Updated never
        // did, while the official plugin saw the same edit. Logging on entry
        // (before the classifier can drop it) is what separates "the event
        // never fired" from "we filtered it out" — the two are otherwise
        // indistinguishable, since neither leaves a trace.
        var item = e.Item;

        // Logged on entry, before the classifier can drop the event: "never
        // fired" and "filtered out" are otherwise indistinguishable, and
        // telling them apart is what settled the phase-5 scare that this
        // recorder was missing ItemUpdated (it was not — the query was wrong).
        // Debug level; a server logging at Information shows nothing here, so
        // raise the server's level rather than this one when diagnosing.
        _logger.LogDebug(
            "Kofin event: {Status} {Kind} {Id} reason={Reason}",
            status,
            item?.GetType().Name,
            item?.Id,
            e.UpdateReason);

        if (item is null)
        {
            return;
        }

        if (!ItemClassifier.TryClassify(item, out var itemType, out var mediaType))
        {
            _logger.LogDebug(
                "Kofin event dropped by classifier: {Kind} {Id} location={Location} source={Source}",
                item.GetType().Name,
                item.Id,
                item.LocationType,
                item.SourceType);
            return;
        }

        Guid? seriesId = null;
        Guid? seasonId = null;

        if (e.Item is Episode episode)
        {
            seriesId = NullIfEmpty(episode.SeriesId);
            seasonId = NullIfEmpty(episode.SeasonId);
        }
        else if (e.Item is Season season)
        {
            seriesId = NullIfEmpty(season.SeriesId);
        }

        var itemEvent = new ItemEvent
        {
            ItemId = e.Item.Id,
            Status = status,
            MediaType = mediaType,
            ItemType = itemType,
            // ItemUpdateType.None is a real bit (1); keep reasons meaningful.
            UpdateReasons = (int)(e.UpdateReason & ~ItemUpdateType.None),
            SeriesId = seriesId,
            SeasonId = seasonId,
            // Resolved here and nowhere else: this is the last point where a
            // *removed* item is still materialised, and the ItemEvent that
            // leaves this method is deliberately BaseItem-free.
            LibraryIds = ResolveLibraries(e, itemType, status),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        bool flushNow;

        lock (_lock)
        {
            _pending.Add(itemEvent);
            flushNow = _pending.Count >= FlushEarlyAt;

            if (!flushNow)
            {
                if (_timer is null)
                {
                    _timer = new Timer(Flush, null, BatchDelayMs, Timeout.Infinite);
                }
                else
                {
                    _timer.Change(BatchDelayMs, Timeout.Infinite);
                }
            }
        }

        if (flushNow)
        {
            Flush(null);
        }
    }

    /// <summary>
    /// The collection folders containing the item — the ids clients whitelist,
    /// the ids <c>/Items/{id}/Ancestors</c> hands them, and the ids Jellyfin
    /// itself checks against <c>EnabledFolders</c>. Deliberately not
    /// <c>GetTopParent()</c>, which answers with a *physical* folder id that no
    /// client can match. Null means unresolvable, never "belongs to nothing".
    /// </summary>
    private List<Guid>? ResolveLibraries(ItemChangeEventArgs e, string itemType, ItemStatus status)
    {
        var item = e.Item;

        if (ItemClassifier.IsCrossLibrary(itemType))
        {
            return null;
        }

        // By the time ItemRemoved fires, DeleteItem has already called
        // item.SetParent(null) (LibraryManager.cs:577) — the item can no
        // longer find its own ancestry, and resolving from it answers "no
        // library" for every removal. The event carries the parent it
        // computed beforehand; that is what a removal has to walk from.
        var anchor = status == ItemStatus.Removed ? e.Parent ?? item : item;

        // One key namespace for both: an episode's ParentId *is* its season
        // id, which is exactly what e.Parent is on the removal path.
        var key = item.ParentId.Equals(default) ? e.Parent?.Id ?? default : item.ParentId;

        return key.Equals(default)
            ? CollectionFolderIds(anchor)
            : _libraryMemo.GetOrAdd(key, _ => CollectionFolderIds(anchor));
    }

    private List<Guid>? CollectionFolderIds(BaseItem item)
    {
        try
        {
            var ids = _libraryManager.GetCollectionFolders(item)
                .Select(folder => folder.Id)
                .ToList();

            return ids.Count == 0 ? null : ids;
        }
        catch (Exception exception)
        {
            // Never lose the record over the dimension: an unresolved library
            // degrades to the pre-v1.1 behaviour, a dropped event does not.
            _logger.LogDebug(exception, "Could not resolve libraries for {Id}", item.Id);
            return null;
        }
    }

    private void Flush(object? state)
    {
        List<ItemEvent> batch;

        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = new List<ItemEvent>(_pending);
            _pending.Clear();
            _libraryMemo.Clear();
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            _store.RecordItemEvents(batch);
            _logger.LogInformation("Recorded {Count} library change events", batch.Count);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to record library change events");
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
            _timer?.Dispose();
        }
    }

    private static Guid? NullIfEmpty(Guid id) => id == Guid.Empty ? null : id;
}
