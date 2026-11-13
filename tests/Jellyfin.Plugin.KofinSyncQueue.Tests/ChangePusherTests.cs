using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Jellyfin.Plugin.KofinSyncQueue.Events;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public class ChangePusherTests
{
    private static ItemEvent Event(Guid id, ItemStatus status)
    {
        return new ItemEvent { ItemId = id, Status = status, MediaType = "movies", ItemType = "Movie" };
    }

    private static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    [Fact]
    public void CoalescesABatchAsTheStoreDoes()
    {
        Guid added = Guid.NewGuid(), removed = Guid.NewGuid(), back = Guid.NewGuid(), edited = Guid.NewGuid();

        var changes = ChangePusher.Coalesce(new[]
        {
            Event(added, ItemStatus.Added),
            Event(removed, ItemStatus.Updated),
            Event(back, ItemStatus.Removed),
            Event(added, ItemStatus.Updated),
            Event(removed, ItemStatus.Removed),
            Event(back, ItemStatus.Added),
            Event(edited, ItemStatus.Updated),
        });

        Assert.Equal(ItemStatus.Added, changes[added]);
        Assert.Equal(ItemStatus.Removed, changes[removed]);
        Assert.Equal(ItemStatus.Added, changes[back]);
        Assert.Equal(ItemStatus.Updated, changes[edited]);
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void NamesWhatTheUserCanSeeAndEveryRemoval()
    {
        Guid seen = Guid.NewGuid(), hidden = Guid.NewGuid(), edited = Guid.NewGuid(), gone = Guid.NewGuid();
        var changes = new Dictionary<Guid, ItemStatus>
        {
            [seen] = ItemStatus.Added,
            [hidden] = ItemStatus.Added,
            [edited] = ItemStatus.Updated,
            [gone] = ItemStatus.Removed,
        };

        var info = ChangePusher.Describe(changes, id => id != hidden && id != gone);

        Assert.Equal(new[] { N(seen) }, info.ItemsAdded);
        Assert.Equal(new[] { N(edited) }, info.ItemsUpdated);
        Assert.Equal(new[] { N(gone) }, info.ItemsRemoved);
        Assert.Empty(info.FoldersAddedTo);
        Assert.Empty(info.CollectionFolders);
    }

    [Fact]
    public void NothingVisibleIsNothingToSend()
    {
        var changes = new Dictionary<Guid, ItemStatus> { [Guid.NewGuid()] = ItemStatus.Updated };

        Assert.True(ChangePusher.Describe(changes, _ => false).IsEmpty);
    }
}
