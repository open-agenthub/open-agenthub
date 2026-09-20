using AgentHub.Api.Webhooks;
using Xunit;

namespace AgentHub.Api.Tests;

public class WebhookDeduplicatorTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void SameKeyWithinWindow_IsDuplicate()
    {
        var dedup = new WebhookDeduplicator();
        Assert.True(dedup.TryBegin("t1:merge_request:12:opened"));
        Assert.False(dedup.TryBegin("t1:merge_request:12:opened"));
    }

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var dedup = new WebhookDeduplicator();
        Assert.True(dedup.TryBegin("t1:merge_request:12:opened"));
        Assert.True(dedup.TryBegin("t1:merge_request:12:reopened"));
        Assert.True(dedup.TryBegin("t1:merge_request:13:opened"));
        Assert.True(dedup.TryBegin("t2:merge_request:12:opened"));
    }

    [Fact]
    public void SameKeyAfterWindow_IsAcceptedAgain()
    {
        var time = new ManualTimeProvider();
        var dedup = new WebhookDeduplicator(time, TimeSpan.FromMinutes(5));

        Assert.True(dedup.TryBegin("k"));
        time.Now += TimeSpan.FromMinutes(4);
        Assert.False(dedup.TryBegin("k"));
        time.Now += TimeSpan.FromMinutes(1);
        Assert.True(dedup.TryBegin("k"));
    }
}
