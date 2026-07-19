# Kofin Sync Queue

Jellyfin server plugin: a **typed change queue** for the Kofin Kodi client
([`plugin.video.kofin`](../plugin.video.kofin)). It records library and
user-data changes while Kodi boxes are offline and serves them as coalesced,
typed records — change reason, series/season parents, a query-time Etag per
record, and the retention cutoff in-band — so a catch-up costs the client
(and this server) as close to zero traffic as the change set allows.

Clean-room implementation (GPL-3). It coexists with the official
KodiSyncQueue plugin: stock jellyfin-kodi clients keep using that one;
Kofin clients probe for this plugin first and fall back to the official
protocol when it is absent.

Requires Jellyfin **10.11+** (`targetAbi 10.11.0.0`).

## Protocol v1

Both endpoints are authorized; the user is derived from the access token —
no user id in the path.

```
GET /Kofin/SyncQueue/Info
→ { "PluginVersion": "1.0.0.0", "ProtocolVersion": 1,
    "ServerTime": <unix>, "RetentionCutoff": <unix|0>, "RetentionDays": 90 }

GET /Kofin/SyncQueue?since=<unix>&types=movies,tvshows,boxsets,musicvideos,music
→ { "ServerTime": <unix>, "RetentionCutoff": <unix|0>,
    "Items": [ { "Id": "<guid-N>", "Status": "Added|Updated|Removed",
                 "MediaType": "movies|tvshows|boxsets|musicvideos|music",
                 "ItemType": "Movie|BoxSet|Series|Season|Episode|MusicVideo|MusicAlbum|MusicArtist|Audio",
                 "LastModified": <unix>, "UpdateReason": "ImageUpdate, MetadataEdit"|null,
                 "Etag": "<md5>"|null, "SeriesId": "<guid-N>"|null, "SeasonId": "<guid-N>"|null } ],
    "UserData": [ <full UserItemDataDto>, … ] }
```

Semantics:

* `since` is required (unix seconds; `0` = everything) and compared
  strictly (`LastModified > since`). Advance your watermark to the
  response's `ServerTime`.
* `types` is an **include** list; absent means all. Unknown tokens are
  ignored (logged) — nothing ever defaults to a media type.
* `Items` is coalesced: one record per item id. Added+Updated stays Added
  (reasons OR'd); anything+Removed becomes Removed.
* `Etag` is computed at **query time** from the live item via
  `BaseItem.GetEtag(user)` — byte-identical to the Etag in every DTO the
  server hands out, so clients can drop records whose Etag matches what
  they already stored *before* downloading anything. Removed records carry
  `Etag: null`.
* Records are visibility-filtered for the calling user; `UserData` rows are
  the caller's only.
* `RetentionCutoff > 0 && since < RetentionCutoff` means records in the gap
  are gone — run a reconciliation pass instead of trusting the window.

## Storage

LiteDB (`kofinsyncqueue/kofinsyncqueue.db` under the server data
directory), indexed on item id and timestamp, one upsert per record —
no full-collection rewrites under scan storms. Retention defaults to
**90 days** (configurable; 0 = keep forever) with a daily cleanup task.

## Build

```
dotnet test
dotnet publish Jellyfin.Plugin.KofinSyncQueue -c Release
```

Install: drop `Jellyfin.Plugin.KofinSyncQueue.dll` + `LiteDB.dll` into a
`plugins/KofinSyncQueue` folder under the server's data directory and
restart, or package with `jprm` using `build.yaml`.
