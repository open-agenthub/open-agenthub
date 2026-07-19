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
        wi.Start("s1", (text, ct) => { lock (frames) frames.Add(text); return Task.CompletedTask; });
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
        wi.Start("s1", (_, _) => Task.CompletedTask);
        wi.Start("s1", (_, _) => Task.CompletedTask); // must not throw or leak
        wi.Stop("s1");
        wi.Stop("s1"); // idempotent
    }
}
