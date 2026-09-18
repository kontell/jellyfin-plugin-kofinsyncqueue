using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Jellyfin.Plugin.KofinSyncQueue.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue.Api;

/// <summary>
/// The Kofin sync-queue protocol v1. Authorized; the user comes from the
/// access token — no user id in the path, nothing to spoof.
/// </summary>
[ApiController]
[Authorize]
[Route("Kofin")]
[Produces(MediaTypeNames.Application.Json)]
public class SyncQueueController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly SyncStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncQueueController> _logger;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    public SyncQueueController(
        SyncStore store,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<SyncQueueController> logger)
    {
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// The probe: protocol version, server clock and retention cutoff in
    /// one round trip.
    /// </summary>
    /// <returns>The <see cref="SyncInfoResponse"/>.</returns>
    [HttpGet("SyncQueue/Info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncInfoResponse> GetInfo()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var retentionDays = KofinSyncQueuePlugin.RetentionDays;

        return new SyncInfoResponse
        {
            PluginVersion = KofinSyncQueuePlugin.Instance?.Version.ToString() ?? "0",
            ProtocolVersion = 1,
            ServerTime = now,
            RetentionCutoff = RetentionMath.CutoffFor(retentionDays, now),
            RetentionDays = retentionDays,
        };
    }

    /// <summary>
    /// Changes since the watermark: coalesced typed records with
    /// query-time Etags, plus the caller's user-data DTOs.
    /// </summary>
    /// <param name="since">Unix-seconds watermark; 0 = everything. Required.</param>
    /// <param name="types">Include list of media-type classes (movies,tvshows,boxsets,musicvideos,music,playlists); absent = all.</param>
    /// <param name="libraries">Include list of collection folder ids; absent = all. Records whose library is unknown are always served.</param>
    /// <returns>The <see cref="SyncQueueResponse"/>.</returns>
    [HttpGet("SyncQueue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SyncQueueResponse> GetSyncQueue(
        [FromQuery] long? since,
        [FromQuery] string? types,
        [FromQuery] string? libraries)
    {
        if (since is null)
        {
            return BadRequest("since is required (unix seconds; 0 = everything)");
        }

        // An api-key request authenticates as the server, not a user, and
        // carries an all-zeros user claim that TryParse happily accepts —
        // GetUserById(Guid.Empty) then throws rather than returning null.
        // The feed is per-user by definition, so that is a 401, not a 500.
        var userIdValue = User.FindFirstValue(UserIdClaim);
        if (!Guid.TryParse(userIdValue, out var userId) || userId.Equals(default))
        {
            return Unauthorized();
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var include = TypesFilter.Parse(types, out var unknown);
        if (unknown.Count > 0)
        {
            // Never guess what an unknown token meant (the legacy empty-
            // filter parse defaulted to Movies; nothing defaults here).
            _logger.LogWarning("Ignoring unknown types tokens: {Tokens}", unknown);
        }

        var libraryFilter = LibrariesFilter.Parse(libraries, out var unparsed);
        if (unparsed.Count > 0)
        {
            _logger.LogWarning("Ignoring unreadable libraries tokens: {Tokens}", unparsed);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = new SyncQueueResponse
        {
            ServerTime = now,
            RetentionCutoff = RetentionMath.CutoffFor(KofinSyncQueuePlugin.RetentionDays, now),
        };

        var learned = new List<ItemRec>();
        var orphaned = new List<Guid>();
        var foreign = 0;

        foreach (var record in _store.ItemsSince(since.Value))
        {
            if (include is not null && !include.Contains(record.MediaType))
            {
                continue;
            }

            // Removals are never library-filtered. They cost the client two
            // indexed local lookups rather than a round trip, so there is
            // nothing to save, and a removal dropped in error leaves a row
            // orphaned in the client's database with no watermark that would
            // ever bring it back. The weakest signal, the worst failure.
            var scoped = record.Status != ItemStatus.Removed;

            // Ahead of the projection: a record for a library the caller does
            // not sync costs neither an item load nor an Etag.
            if (scoped && !LibrariesFilter.Matches(record.LibraryIds, libraryFilter))
            {
                foreign++;
                continue;
            }

            var projected = RecordProjection.Project(record, id =>
            {
                var item = _libraryManager.GetItemById(id);

                // Deleted between event and query; the Removed record that
                // follows (or has coalesced) covers it.
                if (item is null || !item.IsVisibleStandalone(user))
                {
                    return new ItemResolution(false, null);
                }

                // Query time, not event time: an event storm on one item
                // coalesces into a single current-state comparison, and the
                // string is byte-identical to the DTO Etag clients store.
                var etag = item.GetEtag(user);

                if (record.LibraryIds is not null)
                {
                    return new ItemResolution(true, etag);
                }

                return Learn(record, item, etag, learned, orphaned);
            });

            if (projected is null)
            {
                continue;
            }

            // A record that only just learned its libraries has not met the
            // filter yet.
            if (scoped && !LibrariesFilter.Matches(record.LibraryIds, libraryFilter))
            {
                foreign++;
                continue;
            }

            response.Items.Add(projected);
        }

        _store.BackfillLibraries(learned);

        if (orphaned.Count > 0)
        {
            _logger.LogInformation(
                "Dropped {Count} records for items whose library no longer exists",
                _store.DeleteItems(orphaned));
        }

        foreach (var record in _store.UserDataSince(since.Value, userId))
        {
            if (include is not null && !include.Contains(record.MediaType))
            {
                continue;
            }

            var dto = JsonSerializer.Deserialize<UserItemDataDto>(record.Json, _jsonOptions);

            if (dto is not null)
            {
                response.UserData.Add(dto);
            }
        }

        _logger.LogInformation(
            "SyncQueue since {Since}: {Items} records, {UserData} user-data changes, {Foreign} outside the caller's libraries",
            since.Value,
            response.Items.Count,
            response.UserData.Count,
            foreign);

        return response;
    }

    /// <summary>
    /// Resolve the libraries of a record stored before the dimension existed,
    /// and decide what its answer means. This is also what unmasks the ghosts:
    /// removing a Jellyfin library deletes the .mblink directory and fires no
    /// per-item event, so its items stay in the item database, still load and
    /// still pass the visibility check — but they belong to no collection
    /// folder any more, and every client has been failing them ever since.
    /// </summary>
    private ItemResolution Learn(
        ItemRec record,
        BaseItem item,
        string? etag,
        List<ItemRec> learned,
        List<Guid> orphaned)
    {
        // A boxset would resolve to the Collections library, which no client
        // whitelists — stamping it would have them discard every collection.
        if (ItemClassifier.IsCrossLibrary(record.ItemType))
        {
            return new ItemResolution(true, etag);
        }

        var resolved = _libraryManager.GetCollectionFolders(item)
            .Select(folder => folder.Id)
            .ToList();

        if (resolved.Count > 0)
        {
            record.LibraryIds = resolved;
            learned.Add(record);
            return new ItemResolution(true, etag, resolved);
        }

        // Kinds that legitimately have no library are not evidence of
        // anything; anything else with no library is a ghost.
        if (ItemClassifier.MayLackLibrary(record.ItemType))
        {
            return new ItemResolution(true, etag);
        }

        orphaned.Add(record.ItemId);
        return new ItemResolution(false, null);
    }
}
