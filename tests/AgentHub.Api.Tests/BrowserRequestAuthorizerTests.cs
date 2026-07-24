using System.Net;
using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserRequestAuthorizerTests
{
    [Theory]
    [InlineData(null, "session-1", "10.0.0.8", false)]
    [InlineData("wrong", "session-1", "10.0.0.8", false)]
    [InlineData("token-1", "session-2", "10.0.0.8", false)]
    [InlineData("token-1", "session-1", "10.0.0.9", false)]
    [InlineData("token-1", "session-1", "10.0.0.8", true)]
    public async Task Authorize_RequiresMatchingTokenRouteAndLivePod(
        string? token, string routeId, string sourceIp, bool allowed)
    {
        var sessions = new CallbackSessionStore(Session());
        var pods = new FixedPodIdentityResolver("session-1", "10.0.0.8");
        var authorizer = new BrowserRequestAuthorizer(sessions, pods);

        var result = await authorizer.AuthorizeAsync(
            routeId, token, IPAddress.Parse(sourceIp), CancellationToken.None);

        Assert.Equal(allowed, result is not null);
    }

    [Fact]
    public async Task Authorize_NormalizesIpv4MappedIpv6()
    {
        var pods = new FixedPodIdentityResolver("session-1", "10.0.0.8");
        var authorizer = new BrowserRequestAuthorizer(new CallbackSessionStore(Session()), pods);

        var result = await authorizer.AuthorizeAsync(
            "session-1", "token-1", IPAddress.Parse("::ffff:10.0.0.8"));

        Assert.NotNull(result);
    }

    private static SessionRecord Session() => new()
    {
        Id = "session-1", Owner = "owner", CallbackToken = "token-1",
        AgentSessionId = "agent-thread", Mode = SessionMode.Interactive
    };

    private sealed class FixedPodIdentityResolver(string sessionId, string podIp)
        : IAgentPodIdentityResolver
    {
        public Task<bool> IsLiveSessionPodAsync(string candidateSessionId, IPAddress sourceIp,
            CancellationToken ct = default) => Task.FromResult(
                candidateSessionId == sessionId &&
                IpAddressNormalization.Equals(sourceIp, IPAddress.Parse(podIp)));
    }

    private sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(session.Id == id && session.Owner == owner ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([session]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
