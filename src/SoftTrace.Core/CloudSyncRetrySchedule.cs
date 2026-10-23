namespace SoftTrace.Core;

/// <summary>Normal one-minute synchronization, bounded retry delays after transient failures.</summary>
public sealed class CloudSyncRetrySchedule
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitter;
    private DateTimeOffset _next;
    private int _failures;

    public CloudSyncRetrySchedule(TimeProvider? clock = null, Func<double>? jitter = null)
    {
        _clock = clock ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _next = _clock.GetUtcNow();
    }

    public TimeSpan Delay
    {
        get { lock (_gate) return TimeSpan.FromTicks(Math.Max(0, (_next - _clock.GetUtcNow()).Ticks)); }
    }

    public void RecordSuccess()
    {
        lock (_gate) { _failures = 0; _next = _clock.GetUtcNow().AddMinutes(1); }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failures = Math.Min(_failures + 1, 4);
            var minutes = Math.Min(15, Math.Pow(2, _failures) * (0.85 + 0.3 * _jitter()));
            _next = _clock.GetUtcNow().AddMinutes(minutes);
        }
    }

    public void RequestNow()
    {
        lock (_gate) _next = _clock.GetUtcNow();
    }
}
