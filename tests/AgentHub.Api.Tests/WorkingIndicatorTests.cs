using AgentHub.Api.Chat;
using Xunit;

namespace AgentHub.Api.Tests;

public class WorkingIndicatorTests
{
    [Fact]
    public async Task Animates_UntilStopped()
    {
        var frames = new List<string>();
        var wi = new WorkingIndicator(interval: TimeSpan.FromMilliseconds(10), maxDuration: TimeSpan.FromSeconds(5));
        wi.Start("s1", (text, ct) => { lock (frames) frames.Add(text); return Task.FromResult(true); });
        await Task.Delay(100);
        wi.Stop("s1");
        int count; lock (frames) count = frames.Count;
        Assert.True(count >= 2, $"expected >=2 frames, got {count}");
        await Task.Delay(60);
        lock (frames) Assert.Equal(count, frames.Count); // no frames after Stop
    }

    [Fact]
    public void Start_ReplacesExistingLoop_And_StopIsIdempotent()
    {
        var wi = new WorkingIndicator(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        wi.Start("s1", (_, _) => Task.FromResult(true));
        wi.Start("s1", (_, _) => Task.FromResult(true)); // must not throw or leak
        wi.Stop("s1");
        wi.Stop("s1"); // idempotent
    }

    [Fact]
    public async Task Edit_ReturningFalse_StopsLoop()
    {
        var count = 0;
        var wi = new WorkingIndicator(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
        // First edit succeeds, second reports "message gone" — the loop must exit on its own.
        wi.Start("s1", (_, _) => Task.FromResult(Interlocked.Increment(ref count) < 2));
        await Task.Delay(150);
        Assert.Equal(2, Volatile.Read(ref count)); // stopped right after the rejected edit
        await Task.Delay(60);
        Assert.Equal(2, Volatile.Read(ref count)); // …and stayed stopped
    }

    [Fact]
    public async Task Loop_SelfRemoves_AfterMaxDuration()
    {
        var oldFrames = 0; var newFrames = 0;
        var wi = new WorkingIndicator(TimeSpan.FromMilliseconds(10), maxDuration: TimeSpan.FromMilliseconds(50));
        wi.Start("s1", (_, _) => { Interlocked.Increment(ref oldFrames); return Task.FromResult(true); });
        await Task.Delay(150); // maxDuration elapsed — the loop must have removed itself
        var oldCount = Volatile.Read(ref oldFrames);

        wi.Start("s1", (_, _) => { Interlocked.Increment(ref newFrames); return Task.FromResult(true); });
        await Task.Delay(100);
        wi.Stop("s1");
        Assert.True(Volatile.Read(ref newFrames) >= 1, "restart after maxDuration must animate again");
        Assert.Equal(oldCount, Volatile.Read(ref oldFrames)); // the old loop stayed dead
    }
}
