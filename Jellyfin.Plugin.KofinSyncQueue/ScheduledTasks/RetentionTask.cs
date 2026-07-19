using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KofinSyncQueue.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KofinSyncQueue.ScheduledTasks;

/// <summary>
/// Daily cleanup of records older than the retention window.
/// </summary>
public class RetentionTask : IScheduledTask
{
    private readonly SyncStore _store;
    private readonly ILogger<RetentionTask> _logger;

    public RetentionTask(SyncStore store, ILogger<RetentionTask> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Remove old Kofin sync data";

    /// <inheritdoc />
    public string Category => "Kofin Sync Queue";

    /// <inheritdoc />
    public string Description
        => "Removes change records older than the configured retention window.";

    /// <inheritdoc />
    public string Key => "KofinSyncQueueRetention";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
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
