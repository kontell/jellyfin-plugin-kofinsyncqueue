using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.KofinSyncQueue.Events;

/// <summary>
/// Which items the queue records, and how they classify. The nine library
/// kinds plus playlists; virtual and non-library items are noise.
/// </summary>
public static class ItemClassifier
{
    /// <summary>
    /// Classify an item into (ItemType, MediaType class); false = not
    /// recorded.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="itemType">The client type name (Movie, Series, ...).</param>
    /// <param name="mediaType">The media-type class (movies, tvshows, ...).</param>
    /// <returns>Whether the item belongs in the queue.</returns>
    public static bool TryClassify(BaseItem item, out string itemType, out string mediaType)
    {
        itemType = item.GetClientTypeName();
        mediaType = itemType switch
        {
            "Movie" => "movies",
            "Series" or "Season" or "Episode" => "tvshows",
            "BoxSet" => "boxsets",
            "MusicVideo" => "musicvideos",
            "MusicAlbum" or "MusicArtist" or "Audio" => "music",
            "Playlist" => "playlists",
            _ => string.Empty,
        };

        if (mediaType.Length == 0)
        {
            return false;
        }

        if (item.LocationType == LocationType.Virtual || item.SourceType != SourceType.Library)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the kind aggregates across libraries rather than living in
    /// one. A BoxSet sits in the Collections library, which no client
    /// whitelists — stamping that id on the record would have clients
    /// discard every boxset as foreign, so boxsets carry no library at all.
    /// Playlists live in Jellyfin's Playlists folder for the same reason.
    /// </summary>
    /// <param name="itemType">The client type name.</param>
    /// <returns>Whether to leave the record's libraries unset.</returns>
    public static bool IsCrossLibrary(string itemType)
        => string.Equals(itemType, "BoxSet", System.StringComparison.Ordinal)
            || string.Equals(itemType, "Playlist", System.StringComparison.Ordinal);

    /// <summary>
    /// Whether resolving no library for the kind is ordinary rather than
    /// evidence that its library was deleted. Adds MusicArtist to the above:
    /// artists are server-global, and a metadata-only one has no folder to
    /// resolve — which is exactly why clients resolve them through their
    /// content instead. Getting this wrong drops real items, so it errs
    /// towards leaving records alone.
    /// </summary>
    /// <param name="itemType">The client type name.</param>
    /// <returns>Whether an empty resolution is expected for this kind.</returns>
    public static bool MayLackLibrary(string itemType)
        => IsCrossLibrary(itemType)
            || string.Equals(itemType, "MusicArtist", System.StringComparison.Ordinal);
}
