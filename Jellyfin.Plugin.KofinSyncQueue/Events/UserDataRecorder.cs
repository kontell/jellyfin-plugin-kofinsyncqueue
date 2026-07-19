using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue.Events;

/// <summary>
/// Captures UserDataSaved into per-user UserItemDataDto rows. Playback
/// progress ticks are skipped (they would echo every reporting interval);
/// the item's parent rides along so season/series watched-state rollups
/// reach clients without a re-fetch.
/// </summary>
public class UserDataRecorder : IHostedService, IDisposable
{
    private const int BatchDelayMs = 500;
    private const int FlushEarlyAt = 2000;

    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly SyncStore _store;
    private readonly ILogger<UserDataRecorder> _logger;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
    private readonly object _lock = new object();
    private readonly Dictionary<Guid, Dictionary<Guid, BaseItem>> _pending
        = new Dictionary<Guid, Dictionary<Guid, BaseItem>>();

    private Timer? _timer;
    private int _pendingCount;

    public UserDataRecorder(
        IUserDataManager userDataManager,
        IUserManager userManager,
        SyncStore store,
        ILogger<UserDataRecorder> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        Flush(null);
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason == UserDataSaveReason.PlaybackProgress || e.Item is null)
        {
            return;
        }

        if (!ItemClassifier.TryClassify(e.Item, out _, out _))
        {
            return;
        }

        bool flushNow;

        lock (_lock)
        {
            if (!_pending.TryGetValue(e.UserId, out var items))
            {
                items = new Dictionary<Guid, BaseItem>();
                _pending[e.UserId] = items;
            }

            items[e.Item.Id] = e.Item;
            _pendingCount++;

            // Watched-state rollups: the parent's userdata changed too.
            var parent = e.Item.GetParent();
            if (parent is not null && ItemClassifier.TryClassify(parent, out _, out _))
            {
                items[parent.Id] = parent;
                _pendingCount++;
            }

            flushNow = _pendingCount >= FlushEarlyAt;

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
        List<KeyValuePair<Guid, Dictionary<Guid, BaseItem>>> batch;

        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = _pending.ToList();
            _pending.Clear();
            _pendingCount = 0;
            _timer?.Dispose();
            _timer = null;
        }

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var written = 0;

            foreach (var (userId, items) in batch)
            {
                var user = _userManager.GetUserById(userId);

                if (user is null)
                {
                    continue;
                }

                foreach (var item in items.Values)
                {
                    if (!ItemClassifier.TryClassify(item, out _, out var mediaType))
                    {
                        continue;
                    }

                    var dto = _userDataManager.GetUserDataDto(item, user);

                    if (dto is null)
                    {
                        continue;
                    }

                    dto.ItemId = item.Id;
                    _store.RecordUserData(
                        userId,
                        item.Id,
                        mediaType,
                        JsonSerializer.Serialize(dto, _jsonOptions),
                        now);
                    written++;
                }
            }

            _logger.LogInformation("Recorded {Count} user-data changes", written);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to record user-data changes");
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
}
