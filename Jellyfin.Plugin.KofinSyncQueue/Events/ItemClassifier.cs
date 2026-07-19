using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.KofinSyncQueue.Events;

/// <summary>
/// Which items the queue records, and how they classify. Exactly the nine
/// kinds the Kofin client syncs; virtual and non-library items are noise.
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
}
