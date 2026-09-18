using Jellyfin.Plugin.KofinSyncQueue.Events;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public class ItemClassifierTests
{
    [Fact]
    public void PlaylistIsCrossLibraryLikeABoxSet()
    {
        Assert.True(ItemClassifier.IsCrossLibrary("BoxSet"));
        Assert.True(ItemClassifier.IsCrossLibrary("Playlist"));
        Assert.False(ItemClassifier.IsCrossLibrary("Movie"));
        Assert.False(ItemClassifier.IsCrossLibrary("Audio"));
    }

    [Fact]
    public void PlaylistMayLackALibrary()
    {
        Assert.True(ItemClassifier.MayLackLibrary("Playlist"));
        Assert.True(ItemClassifier.MayLackLibrary("BoxSet"));
        Assert.True(ItemClassifier.MayLackLibrary("MusicArtist"));
        Assert.False(ItemClassifier.MayLackLibrary("Movie"));
    }
}
