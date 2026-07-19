using AgentHub.Api.Permissions;
using Xunit;

namespace AgentHub.Api.Tests;

public class PermissionRelayTests
{
    private sealed class FakeNotifier(bool posts) : IPermissionNotifier
    {
        public int Calls { get; private set; }

        public Task<bool> PostAsync(PermissionRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(posts);
        }
    }

    private static PermissionRequest Request() => new()
    {
        Id = "req-1", SessionId = "session-a", Owner = "owner-a", Tool = "Bash"
    };

    [Fact]
    public async Task FirstNotifierPosts_LaterOnesAreNotAsked()
    {
        var first = new FakeNotifier(true);
        var second = new FakeNotifier(true);

        var posted = await PermissionRelay.TryPostAsync([first, second], Request(), CancellationToken.None);

        Assert.True(posted);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task FallsThroughToTheNextNotifier()
    {
        var first = new FakeNotifier(false);
        var second = new FakeNotifier(true);

        var posted = await PermissionRelay.TryPostAsync([first, second], Request(), CancellationToken.None);

        Assert.True(posted);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task NoNotifierPosts_ReturnsFalse()
    {
        var first = new FakeNotifier(false);
        var second = new FakeNotifier(false);

        var posted = await PermissionRelay.TryPostAsync([first, second], Request(), CancellationToken.None);

        Assert.False(posted);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }
}
