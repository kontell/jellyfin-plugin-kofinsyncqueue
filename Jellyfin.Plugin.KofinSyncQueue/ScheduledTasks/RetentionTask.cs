using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue.ScheduledTasks;

/// <summary>
/// Daily cleanup of records older than the retention window.
/// </summary>
public class RetentionTask : IScheduledTask
{
    private readonly SyncStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RetentionTask> _logger;

    public RetentionTask(
        SyncStore store,
        ILibraryManager libraryManager,
        ILogger<RetentionTask> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Remove old Kofin sync data";

    /// <inheritdoc />
    public string Category => "Kofin Sync Queue";

    /// <inheritdoc />
    public string Description
        => "Removes change records older than the configured retention window, "
            + "and records belonging to libraries that no longer exist.";

    /// <inheritdoc />
    public string Key => "KofinSyncQueueRetention";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ReapDeletedLibraries();

        var retentionDays = KofinSyncQueuePlugin.RetentionDays;

        if (retentionDays <= 0)
        {
            _logger.LogInformation("Retention disabled (0 days = keep forever); nothing to do");
            return Task.CompletedTask;
        }

        var cutoff = RetentionMath.CutoffFor(
            retentionDays,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _store.DeleteOlderThan(cutoff);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deleting a Jellyfin library removes the .mblink directory and fires no
    /// per-item event, so nothing tells the queue its records are worthless.
    /// They then outlive the library by the whole retention window, and every
    /// client fetches and fails all of them on every catch-up.
    /// </summary>
    private void ReapDeletedLibraries()
    {
        // The same set GetCollectionFolders matches against, so this and the
        // recorder are speaking about the same ids by construction.
        var live = _libraryManager.GetUserRootFolder()
            .Children
            .Select(child => child.Id)
            .ToHashSet();

        if (live.Count == 0)
        {
            // A server with no libraries at all is far more likely to be a
            // transient read than a genuine wipe; deleting everything on that
            // reading is not a risk worth taking.
            _logger.LogWarning("No libraries resolved; skipping the deleted-library sweep");
            return;
        }

        _store.DeleteWithDeadLibraries(live);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            },
        };
    }
}
