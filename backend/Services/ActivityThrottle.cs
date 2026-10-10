namespace AgentHub.Api.Services;

/// <summary>
/// Collapses a stream of "the user did something" signals into at most one touch per interval.
/// A terminal socket sees a frame per keystroke; writing <c>last_activity_at</c> for each would
/// turn an hour of typing into thousands of row updates for a timestamp whose consumers read it
/// once a minute. The first signal goes through at once so a single reply still counts.
/// </summary>
public sealed class ActivityThrottle
{
    private readonly Func<CancellationToken, Task> _touch;
    private readonly TimeSpan _interval;
    private readonly Func<DateTime> _clock;
    private DateTime _last = DateTime.MinValue;
    private readonly object _gate = new();

    public ActivityThrottle(Func<CancellationToken, Task> touch, TimeSpan interval, Func<DateTime>? clock = null)
    {
        _touch = touch;
        _interval = interval;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Touches if the interval has passed since the last touch; otherwise does nothing.
    /// Never throws: a failed touch must not take the socket it rides on down with it.</summary>
    public async Task SignalAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = _clock();
            if (now - _last < _interval) return;
            _last = now;
        }
        try { await _touch(ct); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }
}
