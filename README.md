# Kofin Sync Queue

Jellyfin server plugin: a **typed change queue** for the Kofin Kodi client ([`plugin.video.kofin`](https://github.com/kontell/plugin.video.kofin)). It records library and user-data changes while Kodi boxes are offline and serves them as coalesced, typed records — change reason, series/season parents, a query-time Etag per record, and the retention cutoff in-band — so a catch-up costs the client (and this server) as close to zero traffic as the change set allows.

Clean-room implementation (GPL-3). It coexists with the official KodiSyncQueue plugin: stock jellyfin-kodi clients keep using that one; Kofin clients probe for this plugin first and fall back to the official protocol when it is absent.

Requires Jellyfin **10.11+**. One source tree ships to both server lines: every release carries a `10.11.0.N` zip (`targetAbi 10.11.0.0`, net9.0) and a `12.0.0.N` zip (`targetAbi 12.0.0.0`, net10.0), and a server installs the newest one it can run — Jellyfin reads `targetAbi` as a floor, so the build numbers move in lockstep to keep a v12 server off the 10.11 build. The supported lines are defined in one place, [`.github/workflows/abis.yml`](.github/workflows/abis.yml), which both the PR gate and the release workflow read.

## Protocol v1

Both endpoints are authorized; the user is derived from the access token — no user id in the path.

```
GET /Kofin/SyncQueue/Info
→ { "PluginVersion": "10.11.0.3", "ProtocolVersion": 1,
    "ServerTime": <unix>, "RetentionCutoff": <unix|0>, "RetentionDays": 90,
    "Features": ["library-scope"] }

GET /Kofin/SyncQueue?since=<unix>&types=movies,tvshows,boxsets,musicvideos,music
                    &libraries=<guid-N>,<guid-N>
→ { "ServerTime": <unix>, "RetentionCutoff": <unix|0>,
    "Items": [ { "Id": "<guid-N>", "Status": "Added|Updated|Removed",
                 "MediaType": "movies|tvshows|boxsets|musicvideos|music",
                 "ItemType": "Movie|BoxSet|Series|Season|Episode|MusicVideo|MusicAlbum|MusicArtist|Audio",
                 "LastModified": <unix>, "UpdateReason": "ImageUpdate, MetadataEdit"|null,
                 "Etag": "<md5>"|null, "SeriesId": "<guid-N>"|null, "SeasonId": "<guid-N>"|null,
                 "LibraryIds": ["<guid-N>", …]|null } ],
    "UserData": [ <full UserItemDataDto>, … ] }
```

Semantics:

* `since` is required (unix seconds; `0` = everything) and compared strictly (`LastModified > since`). Advance your watermark to the response's `ServerTime`.
* `types` is an **include** list; absent means all. Unknown tokens are ignored (logged) — nothing ever defaults to a media type.
* `libraries` is an **include** list of collection folder ids — the ids `/UserViews` and `/Items/{id}/Ancestors` report, and the ids a client whitelists. Absent means all; unreadable tokens are ignored (logged). Records whose `LibraryIds` is absent are **always** served, and removals are never library-filtered.
* `LibraryIds` is what a whitelisting client needs to drop a record *before* fetching anything. Absent means unknown, never "belongs to nothing": boxsets carry none (they live in Collections, which no client syncs), folder-less artists resolve to none, and records stored before this field existed learn it the first time they are queried. It is a list because an item under two libraries belongs to both — `/Items/{id}/Ancestors` reports only the first, which is why resolving through it can pick the library you did not whitelist.
* `Features` names optional capabilities on top of the protocol version. Clients require an exact `ProtocolVersion` match, so this list is how a capability is added without demoting every deployed client — read it by presence, and treat an absent list as a server that has none.
* `Items` is coalesced: one record per item id. Added+Updated stays Added (reasons OR'd); anything+Removed becomes Removed.
* `Etag` is computed at **query time** from the live item via `BaseItem.GetEtag(user)` — byte-identical to the Etag in every DTO the server hands out (verified live against a real item), so clients can drop records whose Etag matches what they already stored *before* downloading anything. Removed records carry no Etag.
* **Null fields are omitted, not emitted as `null`** — the server's serializer ignores nulls, so `Etag`, `UpdateReason`, `SeriesId` and `SeasonId` are simply absent when they do not apply. Read them with an absent-tolerant accessor; do not require the key.
* Records are visibility-filtered for the calling user; `UserData` rows are the caller's only.
* `RetentionCutoff > 0 && since < RetentionCutoff` means records in the gap are gone — run a reconciliation pass instead of trusting the window.

## Storage

LiteDB (`kofinsyncqueue/kofinsyncqueue.db` under the server data directory), indexed on item id and timestamp, one upsert per record — no full-collection rewrites under scan storms. Retention defaults to **90 days** (configurable; 0 = keep forever) with a daily cleanup task.

The same daily task reaps records whose libraries are all gone. Deleting a Jellyfin library removes the `.mblink` directory and fires no per-item event, so nothing else would ever tell the queue those records are worthless — they would outlive the library by the whole retention window, and every client would fetch and fail all of them on every catch-up.

## Build

```
dotnet test
dotnet publish Jellyfin.Plugin.KofinSyncQueue -c Release
tools/package.sh            # → dist/kofin-sync-queue_<version>.zip
```

A bare `tools/package.sh` builds the primary row (10.11) described in `build.yaml`. Any other row is four environment variables, which is all the build matrix passes it:

```
ABI_BASE=12.0.0 TARGET_ABI=12.0.0.0 FRAMEWORK=net10.0 JELLYFIN_VERSION=12.0.0-rc5 \
    tools/package.sh        # → dist/kofin-sync-queue_12.0.0.<build>.zip
```

## Install

Add the Kontell plugin repository, then install from the catalog — Jellyfin unpacks the plugin into the right place (with the right ownership) itself:

1. Dashboard → Plugins → Repositories → **+**, with the URL `https://repository.kontell.workers.dev/jellyfin/manifest.json`
2. Dashboard → Plugins → Catalog → **Kofin Sync Queue** → Install, then restart the server.

Or install manually — unzip into a versioned folder under the server's plugin directory and restart:

```
sudo unzip kofin-sync-queue_10.11.0.3.zip \
    -d "/var/lib/jellyfin/plugins/Kofin Sync Queue_10.11.0.3"
sudo chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/Kofin Sync Queue_10.11.0.3"
sudo systemctl restart jellyfin
```
