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
    private readonly SyncStore _store;
    private readonly ILogger<ChangePusher> _logger;

    public ChangePusher(
        ISessionManager sessionManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        SyncStore store,
        ILogger<ChangePusher> logger)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Send every Kofin session the batch's changes its user may see, once a
    /// feed read serves the batch, so a client that catches up on the
    /// message finds what it names. Never throws: a session that misses the
    /// message finds the records at its next catch-up.
    /// </summary>
    /// <param name="batch">The batch as recorded, in occurrence order.</param>
    /// <param name="stamp">The batch's stamp (<see cref="SyncStore.RecordItemEvents"/>).</param>
    /// <returns>A task that completes once every message is sent.</returns>
    public async Task PushAsync(IReadOnlyList<ItemEvent> batch, long stamp)
    {
        try
        {
            if (KofinSessions().Count == 0)
            {
                return;
            }

            await _store.WaitUntilServedAsync(stamp, CancellationToken.None).ConfigureAwait(false);

            // Whoever is connected now, not before the wait.
            var sessions = KofinSessions();
            var changes = Coalesce(batch);
            var messages = new Dictionary<Guid, LibraryUpdateInfo?>();
            var messageId = Guid.NewGuid();
            var told = 0;

            foreach (var session in sessions)
            {
                if (!messages.TryGetValue(session.UserId, out var info))
                {
                    info = MessageFor(session.UserId, changes);
                    messages[session.UserId] = info;
                }

                if (info is not null && await SendAsync(session, messageId, info).ConfigureAwait(false))
                {
                    told++;
                }
            }

            _logger.LogDebug(
                "Told {Sessions} Kofin session(s) about {Count} library change(s)",
                told,
                changes.Count);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not tell Kofin sessions about library changes");
        }
    }

    /// <summary>
    /// This batch's net status for each item, folded from nothing by the
    /// store's status rule (<see cref="RecordMerge.Fold"/>). The store folds
    /// each event into the record it already holds, so an item it recorded
    /// as added earlier stays added there when this batch edits it; here the
    /// edit is an update, and a client fetches an item named as updated.
    /// </summary>
    /// <param name="batch">The events, in occurrence order.</param>
    /// <returns>Each item's status after the batch.</returns>
    public static IReadOnlyDictionary<Guid, ItemStatus> Coalesce(IEnumerable<ItemEvent> batch)
    {
        var statuses = new Dictionary<Guid, ItemStatus>();

        foreach (var itemEvent in batch)
        {
            statuses[itemEvent.ItemId] = RecordMerge.Fold(
                statuses.TryGetValue(itemEvent.ItemId, out var status) ? (ItemStatus?)status : null,
                itemEvent.Status);
        }

        return statuses;
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

    private List<SessionInfo> KofinSessions()
    {
        return _sessionManager.Sessions
            .Where(s => string.Equals(s.Client, KofinClient, StringComparison.OrdinalIgnoreCase)
                && !s.UserId.Equals(Guid.Empty))
            .ToList();
    }

    /// <summary>The user's message, or null when there is nothing to send.</summary>
    private LibraryUpdateInfo? MessageFor(Guid userId, IReadOnlyDictionary<Guid, ItemStatus> changes)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var info = Describe(changes, id => _libraryManager.GetItemById(id)?.IsVisibleStandalone(user) == true);
        return info.IsEmpty ? null : info;
    }

    /// <summary>
    /// The message to this session alone, through its own controllers:
    /// Jellyfin's per-device send writes to every session that shares the
    /// device id, whatever its client or user.
    /// </summary>
    private async Task<bool> SendAsync(SessionInfo session, Guid messageId, LibraryUpdateInfo info)
    {
        try
        {
            foreach (var controller in session.SessionControllers)
            {
                await controller.SendMessage(SessionMessageType.LibraryChanged, messageId, info, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not tell Kofin session {Session} about library changes", session.Id);
            return false;
        }
    }
}
