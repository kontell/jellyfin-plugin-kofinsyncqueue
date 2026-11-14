using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

/// <summary>
/// A clock that moves only when a test moves it, and fires the one-shot
/// timers it passes (<c>Task.Delay</c>'s), on the caller's thread.
/// </summary>
internal sealed class SteppedClock : TimeProvider
{
    private readonly object _lock = new object();
    private readonly List<Timer> _timers = new List<Timer>();
    private DateTimeOffset _now;

    public SteppedClock(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_lock)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
            _timers.RemoveAll(due.Contains);
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Schedule(Timer timer, TimeSpan dueTime)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.Due = _now + dueTime;
                _timers.Add(timer);
            }
        }
    }

    private sealed class Timer : ITimer
    {
        private readonly SteppedClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public Timer(SteppedClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset Due { get; set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _clock.Schedule(this, dueTime);
            return true;
        }

        public void Dispose() => _clock.Schedule(this, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
