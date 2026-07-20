using System;
using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.KofinSyncQueue.Data;
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
    /// <param name="types">Include list of media-type classes (movies,tvshows,boxsets,musicvideos,music); absent = all.</param>
    /// <returns>The <see cref="SyncQueueResponse"/>.</returns>
    [HttpGet("SyncQueue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<SyncQueueResponse> GetSyncQueue(
        [FromQuery] long? since,
        [FromQuery] string? types)
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

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = new SyncQueueResponse
        {
            ServerTime = now,
            RetentionCutoff = RetentionMath.CutoffFor(KofinSyncQueuePlugin.RetentionDays, now),
        };

        foreach (var record in _store.ItemsSince(since.Value))
        {
            if (include is not null && !include.Contains(record.MediaType))
            {
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
                return new ItemResolution(true, item.GetEtag(user));
            });

            if (projected is null)
            {
                continue;
            }

            response.Items.Add(projected);
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
            "SyncQueue since {Since}: {Items} records, {UserData} user-data changes",
            since.Value,
            response.Items.Count,
            response.UserData.Count);

        return response;
    }
}
