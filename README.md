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
  server hands out (verified live against a real item), so clients can drop
  records whose Etag matches what they already stored *before* downloading
  anything. Removed records carry no Etag.
* **Null fields are omitted, not emitted as `null`** — the server's
  serializer ignores nulls, so `Etag`, `UpdateReason`, `SeriesId` and
  `SeasonId` are simply absent when they do not apply. Read them with an
  absent-tolerant accessor; do not require the key.
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
tools/package.sh            # → dist/kofin-sync-queue_<version>.zip
```

## Install

Unzip into a versioned folder under the server's plugin directory and
restart:

```
sudo unzip kofin-sync-queue_1.0.0.0.zip \
    -d "/var/lib/jellyfin/plugins/Kofin Sync Queue_1.0.0.0"
sudo chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/Kofin Sync Queue_1.0.0.0"
sudo systemctl restart jellyfin
```

**The `chown` is not optional.** Jellyfin rewrites `meta.json` on load to
stamp the plugin's status, and `PluginManager.CreatePluginInstance` does
not catch the failure — a plugin directory the server user cannot write
takes the **whole server down at startup** with
`UnauthorizedAccessException ... meta.json` (not a plugin-load warning,
a fatal `StartServer` error). Unzipping as root is enough to cause it.

Verify the plugin is live without opening the dashboard:

```
curl -s "$SERVER/Kofin/SyncQueue/Info?api_key=$KEY"
```
