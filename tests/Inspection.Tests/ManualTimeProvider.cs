using System.Threading.Channels;

namespace Inspection.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly Channel<ManualTimer> _created = Channel.CreateUnbounded<ManualTimer>();
    private DateTimeOffset _utcNow = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    internal bool ThrowOnCreate { get; set; }
    internal ManualTimer? LastTimer { get; private set; }
    internal int ActiveTimerCount { get { lock (_gate) { return _timers.Count(timer => !timer.Disposed); } } }
    internal Task<ManualTimer> NextTimerAsync() => _created.Reader.ReadAsync().AsTask();

    public override DateTimeOffset GetUtcNow() { lock (_gate) { return _utcNow; } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (ThrowOnCreate) { throw new InvalidOperationException("Timer creation failed."); }
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            LastTimer = timer;
            _created.Writer.TryWrite(timer);
            return timer;
        }
    }

    internal void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ManualTimer[] due;
        lock (_gate)
        {
            _utcNow += duration;
            due = _timers.Where(timer => !timer.Disposed && timer.DueAt <= _utcNow).ToArray();
            foreach (ManualTimer timer in due) { timer.DueAt = DateTimeOffset.MaxValue; }
        }
        foreach (ManualTimer timer in due) { timer.Fire(); }
    }

    internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset DueAt { get; set; } = DateTimeOffset.MaxValue;
        internal bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) { throw new NotSupportedException("Tests use one-shot timers only."); }
            lock (owner._gate)
            {
                if (Disposed) { return false; }
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._utcNow + dueTime;
                return true;
            }
        }

        // Also used to simulate a late callback that was already queued before disposal.
        internal void Fire() => callback(state);
        public void Dispose() { lock (owner._gate) { Disposed = true; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
