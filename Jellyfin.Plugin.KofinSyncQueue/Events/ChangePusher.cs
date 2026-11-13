using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue.Events;

/// <summary>
/// Tells each Kofin session what a recorded batch changed, in a
/// <c>LibraryChanged</c> message shaped like Jellyfin's own, as soon as the
/// feed serves the batch. Jellyfin sends its own only once the library has
/// been quiet for <c>LibraryUpdateDuration</c> (30 s by default, restarted by
/// every change), where a batch is recorded the recorder's debounce after its
/// last event. Other clients are left to Jellyfin's message.
/// </summary>
public class ChangePusher
{
    /// <summary>The client name Kofin's sessions report.</summary>
    public const string KofinClient = "Kofin";

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChangePusher> _logger;
    private readonly TimeProvider _clock;

    public ChangePusher(
        ISessionManager sessionManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<ChangePusher> logger,
        TimeProvider? clock = null)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Send every Kofin session the batch's changes it may see. Never throws:
    /// a client that misses the message still finds the records at its next
    /// catch-up.
    /// </summary>
    /// <param name="batch">The batch as recorded, in occurrence order.</param>
    /// <param name="stamp">The batch's stamp (<see cref="SyncStore.RecordItemEvents"/>).</param>
    /// <returns>A task that completes once every message is sent.</returns>
    public async Task PushAsync(IReadOnlyList<ItemEvent> batch, long stamp)
    {
        try
        {
            var sessions = _sessionManager.Sessions
                .Where(s => string.Equals(s.Client, KofinClient, StringComparison.OrdinalIgnoreCase)
                    && !s.UserId.Equals(Guid.Empty)
                    && !string.IsNullOrEmpty(s.DeviceId))
                .ToList();

            if (sessions.Count == 0)
            {
                return;
            }

            // A feed read serves a batch from the second after its stamp on
            // (SyncStore.Snapshot): a client that caught up any sooner would
            // not find what it was told about.
            var due = DateTimeOffset.FromUnixTimeSeconds(stamp + 1) - _clock.GetUtcNow();
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due, _clock, CancellationToken.None).ConfigureAwait(false);
            }

            var changes = Coalesce(batch);

            foreach (var byUser in sessions.GroupBy(s => s.UserId))
            {
                var user = _userManager.GetUserById(byUser.Key);
                if (user is null)
                {
                    continue;
                }

                var info = Describe(
                    changes,
                    id => _libraryManager.GetItemById(id)?.IsVisibleStandalone(user) == true);

                if (info.IsEmpty)
                {
                    continue;
                }

                foreach (var deviceId in byUser.Select(s => s.DeviceId).Distinct(StringComparer.Ordinal))
                {
                    await _sessionManager.SendMessageToUserDeviceSessions(
                            deviceId,
                            SessionMessageType.LibraryChanged,
                            info,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            _logger.LogDebug(
                "Told {Sessions} Kofin session(s) about {Count} library change(s)",
                sessions.Count,
                changes.Count);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not tell Kofin sessions about library changes");
        }
    }

    /// <summary>
    /// One status per item, by the rules the store coalesces by
    /// (<see cref="RecordMerge"/>).
    /// </summary>
    /// <param name="batch">The events, in occurrence order.</param>
    /// <returns>Each item's status after the batch.</returns>
    public static IReadOnlyDictionary<Guid, ItemStatus> Coalesce(IEnumerable<ItemEvent> batch)
    {
        var records = new Dictionary<Guid, ItemRec>();

        foreach (var itemEvent in batch)
        {
            records.TryGetValue(itemEvent.ItemId, out var existing);
            records[itemEvent.ItemId] = RecordMerge.Apply(existing, itemEvent, 0);
        }

        return records.ToDictionary(pair => pair.Key, pair => pair.Value.Status);
    }

    /// <summary>
    /// The message for one user, by Jellyfin's own rule: an added or updated
    /// item only if the user can see it, and every removal, since a removed
    /// item can no longer be asked.
    /// </summary>
    /// <param name="changes">Each item's status (<see cref="Coalesce"/>).</param>
    /// <param name="visible">Whether the user can see an item.</param>
    /// <returns>The message; <c>IsEmpty</c> when there is nothing to send.</returns>
    public static LibraryUpdateInfo Describe(
        IReadOnlyDictionary<Guid, ItemStatus> changes,
        Func<Guid, bool> visible)
    {
        string[] Ids(ItemStatus status, bool checkVisible) => changes
            .Where(pair => pair.Value == status && (!checkVisible || visible(pair.Key)))
            .Select(pair => pair.Key.ToString("N", CultureInfo.InvariantCulture))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return new LibraryUpdateInfo
        {
            ItemsAdded = Ids(ItemStatus.Added, true),
            ItemsUpdated = Ids(ItemStatus.Updated, true),
            ItemsRemoved = Ids(ItemStatus.Removed, false),
        };
    }
}
