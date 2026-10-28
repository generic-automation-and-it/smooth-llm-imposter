namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

/// <summary>
/// Manual clock for admission waits: a timer fires only on <see cref="Advance"/>, and
/// <see cref="TimerCreated"/> completes when a waiter starts its deadline, so a test knows a call is queued
/// at the gate without sleeping.
/// </summary>
internal sealed class AdmissionClock : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource _timerCreated = NewSignal();
    private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public Task TimerCreated
    {
        get
        {
            lock (_lock)
            {
                return _timerCreated.Task;
            }
        }
    }

    public int ActiveTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        TaskCompletionSource created;
        lock (_lock)
        {
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + dueTime;
            _timers.Add(timer);
            created = _timerCreated;
            _timerCreated = NewSignal();
        }

        created.TrySetResult();
        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += delta;
            due = _timers.Where(timer => timer.DueAt <= _now).ToList();
            foreach (ManualTimer timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer(AdmissionClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
