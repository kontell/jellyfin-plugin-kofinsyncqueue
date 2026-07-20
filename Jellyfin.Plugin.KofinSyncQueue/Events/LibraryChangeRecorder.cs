using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KofinSyncQueue.Data;
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
