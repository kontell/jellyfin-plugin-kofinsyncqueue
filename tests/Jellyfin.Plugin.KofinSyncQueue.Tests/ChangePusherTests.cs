using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using Jellyfin.Plugin.KofinSyncQueue.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

public sealed class ChangePusherTests : IDisposable
{
    // The batch's stamp; the clock starts part way into that second.
    private const long Stamp = 1_000_000;

    private static readonly TimeSpan Late = TimeSpan.FromSeconds(5);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kofinsq-tests", Guid.NewGuid().ToString("N"));
    private readonly SteppedClock _clock = new SteppedClock(DateTimeOffset.FromUnixTimeSeconds(Stamp).AddMilliseconds(200));
    private readonly List<SessionInfo> _sessions = new List<SessionInfo>();
    private readonly ISessionManager _sessionManager;
    private readonly SyncStore _store;
    private readonly ChangePusher _pusher;

    public ChangePusherTests()
    {
        _sessionManager = Fake.Of<ISessionManager>(new Dictionary<string, Func<MethodInfo, object?[], object?>>
        {
            ["get_Sessions"] = (_, _) => _sessions.ToList(),
        });
        var users = Fake.Of<IUserManager>(new Dictionary<string, Func<MethodInfo, object?[], object?>>
        {
            // Every user exists; with removals alone, nothing asks what one may see.
            ["GetUserById"] = (method, _) => RuntimeHelpers.GetUninitializedObject(method.ReturnType),
        });
        var library = Fake.Of<ILibraryManager>(new Dictionary<string, Func<MethodInfo, object?[], object?>>());
        _store = new SyncStore(_directory, NullLogger<SyncStore>.Instance, _clock);
        _pusher = new ChangePusher(_sessionManager, users, library, _store, NullLogger<ChangePusher>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static ItemEvent Event(Guid id, ItemStatus status)
    {
        return new ItemEvent { ItemId = id, Status = status, MediaType = "movies", ItemType = "Movie" };
    }

    private static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private Controller Connect(string client, string deviceId, Guid? userId = null, bool fails = false)
    {
        var controller = new Controller(fails);
        _sessions.Add(new SessionInfo(_sessionManager, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Client = client,
            DeviceId = deviceId,
            UserId = userId ?? Guid.NewGuid(),
            SessionControllers = new ISessionController[] { controller },
        });
        return controller;
    }

    private void ToTheNextSecond() => _clock.Advance(TimeSpan.FromMilliseconds(800));

    [Fact]
    public void GivesEachItemTheBatchsNetStatus()
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
        // From this batch alone: an edit is an update, whatever the store
        // recorded for the item before.
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

    [Fact]
    public async Task SendsOnceAFeedReadServesTheBatch()
    {
        var gone = Guid.NewGuid();
        var kodi = Connect(ChangePusher.KofinClient, "kodi");

        var push = _pusher.PushAsync(new[] { Event(gone, ItemStatus.Removed) }, Stamp);
        _clock.Advance(TimeSpan.FromMilliseconds(799));

        Assert.False(push.IsCompleted);
        Assert.Empty(kodi.Sent);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await push.WaitAsync(Late);

        var (name, data) = Assert.Single(kodi.Sent);
        Assert.Equal(SessionMessageType.LibraryChanged, name);
        Assert.Equal(new[] { N(gone) }, Assert.IsType<LibraryUpdateInfo>(data).ItemsRemoved);
    }

    [Fact]
    public async Task WritesOnlyTheKofinSessionOfADevice()
    {
        var kofin = Connect(ChangePusher.KofinClient, "shared");
        var other = Connect("Jellyfin Web", "shared");

        var push = _pusher.PushAsync(new[] { Event(Guid.NewGuid(), ItemStatus.Removed) }, Stamp);
        ToTheNextSecond();
        await push.WaitAsync(Late);

        Assert.Single(kofin.Sent);
        Assert.Empty(other.Sent);
    }

    [Fact]
    public async Task ASessionThatFailsLeavesTheNextOneTold()
    {
        var user = Guid.NewGuid();
        var broken = Connect(ChangePusher.KofinClient, "kodi-a", user, fails: true);
        var next = Connect(ChangePusher.KofinClient, "kodi-b", user);

        var push = _pusher.PushAsync(new[] { Event(Guid.NewGuid(), ItemStatus.Removed) }, Stamp);
        ToTheNextSecond();
        await push.WaitAsync(Late);

        Assert.Empty(broken.Sent);
        Assert.Single(next.Sent);
    }

    [Fact]
    public async Task TellsTheSessionsConnectedOnceTheWaitEnds()
    {
        var left = Connect(ChangePusher.KofinClient, "kodi-a");

        var push = _pusher.PushAsync(new[] { Event(Guid.NewGuid(), ItemStatus.Removed) }, Stamp);
        _sessions.Clear();
        var joined = Connect(ChangePusher.KofinClient, "kodi-b");
        ToTheNextSecond();
        await push.WaitAsync(Late);

        Assert.Empty(left.Sent);
        Assert.Single(joined.Sent);
    }

    [Fact]
    public void WithNoKofinSessionNothingWaits()
    {
        Connect("Jellyfin Web", "browser");

        var push = _pusher.PushAsync(new[] { Event(Guid.NewGuid(), ItemStatus.Removed) }, Stamp);

        Assert.True(push.IsCompleted);
    }

    private sealed class Controller : ISessionController
    {
        private readonly bool _fails;

        public Controller(bool fails)
        {
            _fails = fails;
        }

        public List<(SessionMessageType Name, object? Data)> Sent { get; } = new List<(SessionMessageType, object?)>();

        public bool IsSessionActive => true;

        public bool SupportsMediaControl => false;

        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
        {
            if (_fails)
            {
                throw new ObjectDisposedException(nameof(Controller));
            }

            Sent.Add((name, data));
            return Task.CompletedTask;
        }
    }
}
