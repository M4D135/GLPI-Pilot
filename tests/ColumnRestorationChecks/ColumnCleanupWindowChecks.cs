using GlpiPilot.Core;

internal static class ColumnCleanupWindowChecks
{
    public static void Run()
    {
        var clock = new ManualClock();
        using var cleanup = new TemporaryColumnCleanupWindow(TimeSpan.FromSeconds(180), clock);
        for (var removal = 0; removal < 10; removal++)
        {
            clock.Advance(TimeSpan.FromSeconds(150));
            Require(!cleanup.Token.IsCancellationRequested,
                "The global start time must not cancel later, progressing native removals.");
            cleanup.ConfirmProgress();
        }
        clock.Advance(TimeSpan.FromSeconds(179));
        Require(!cleanup.Token.IsCancellationRequested, "The last verified step must get its own bounded cleanup allowance.");
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(cleanup.Token.IsCancellationRequested, "Without another verified step cleanup must expire, even after prior progress.");
        cleanup.ConfirmProgress();
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(cleanup.Token.IsCancellationRequested, "A late response must never resurrect an expired cleanup run.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? _due;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Cleanup must use a one-shot cancellation timer.");
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._elapsed + dueTime;
                return true;
            }

            public void FireIfDue()
            {
                if (_disposed || _due is null || _due > clock._elapsed) return;
                _due = null; callback(state);
            }

            public void Dispose() { _disposed = true; _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
