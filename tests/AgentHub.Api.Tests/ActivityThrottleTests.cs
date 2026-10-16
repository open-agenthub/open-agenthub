using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The per-socket throttle in front of <c>TouchActivityAsync</c>: a terminal sends a frame per
/// keystroke, and the timestamp behind it is read once a minute.
/// </summary>
public class ActivityThrottleTests
{
    [Fact]
    public async Task FirstSignalTouchesAtOnce_RepeatsInsideTheIntervalDoNot()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var touches = 0;
        var throttle = new ActivityThrottle(_ => { touches++; return Task.CompletedTask; },
            TimeSpan.FromMinutes(1), () => now);

        await throttle.SignalAsync();
        now = now.AddSeconds(10);
        await throttle.SignalAsync();
        now = now.AddSeconds(49);
        await throttle.SignalAsync();
        Assert.Equal(1, touches);

        now = now.AddSeconds(1);
        await throttle.SignalAsync();
        Assert.Equal(2, touches);
    }

    [Fact]
    public async Task AFailedTouch_DoesNotTakeTheSocketDown()
    {
        var throttle = new ActivityThrottle(_ => throw new InvalidOperationException("db down"), TimeSpan.Zero);

        await throttle.SignalAsync();
        await throttle.SignalAsync();
    }
}
