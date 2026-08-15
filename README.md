# Kofin Sync Queue

Jellyfin server plugin: a typed change queue for the Kofin Kodi client [plugin.video.kofin](https://github.com/kontell/plugin.video.kofin). It records library and user-data changes while Kodi boxes are offline and serves them as coalesced, typed records - change reason, series/season parents, a query-time Etag per record, and the retention cutoff in-band - so a catch-up costs the client (and the server) as close to zero traffic as the change set allows.

Clean-room re-implementation (GPL-3). It coexists with the official KodiSyncQueue plugin: jellyfin-kodi clients keep using that one; Kofin clients probe for this plugin first and fall back to the official protocol when it is absent.

Requires Jellyfin 10.11+.

## Installation

Add the Kontell plugin repository, then install from the catalog - Jellyfin unpacks the plugin into the right place (with the right ownership) itself:

1.  Dashboard -> Plugins -> Manange Repositoires -> New Repository: https://repository.kontell.workers.dev/jellyfin/manifest.json
2.  Dashboard -> Plugins -> Install Kofin Sync Queue, then restart the server.

## Protocol

Both endpoints are authorized; the user is derived from the access token - no user id in the path.

- since is required (unix seconds; 0 = everything and compared strictly (LastModified > since). Advance your watermark to the response's ServerTime.
- types is an include list; absent means all. Unknown tokens are ignored (logged) - nothing ever defaults to a media type.
- Items is coalesced: one record per item id. Added+Updated stays Added (reasons OR'd); anything+Removed becomes Removed.
- Etag is computed at query time from the live item via BaseItem.GetEtag(user) - byte-identical to the Etag in every DTO the server hands out (verified live against a real item), so clients can drop records whose Etag matches what they already stored before downloading anything. Removed records carry no Etag.
- Null fields are omitted, not emitted as null - the server's serializer ignores nulls, so Etag, UpdateReason, SeriesId\\ and SeasonId are simply absent when they do not apply. Read them with an absent-tolerant accessor; do not require the key.
- Records are visibility-filtered for the calling user; UserData rows are the caller's only.
- RetentionCutoff > 0 & since < RetentionCutoff means records in the gap are gone - run a reconciliation pass instead of trusting the window.

## Storage

LiteDB (kofinsyncqueue/kofinsyncqueue.db under the server data directory), indexed on item id and timestamp, one upsert per record - no full-collection rewrites under scan storms. Retention defaults to 90 days (configurable; 0 = keep forever) with a daily cleanup task.
