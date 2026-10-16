using System.Net;
using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>Switching a live session to another provider account — docs/provider-accounts.md.</summary>
public sealed class SessionCredentialSwitchTests
{
    private static SessionRecord Record(AgentAuthMode auth = AgentAuthMode.Subscription, SessionMode mode = SessionMode.Interactive) => new()
    {
        Id = "s1", Owner = "alice", CallbackToken = "tok", Agent = AgentKind.Claude, AuthMode = auth, Mode = mode
    };

    [Fact]
    public void Validate_AcceptsARunningSubscriptionSessionWithAnAccount()
        => SessionCredentialSwitch.Validate(Record(), SessionStatus.Running, "10.0.0.5", "abcd1234");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not-Valid")]
    public void Validate_RejectsAMissingOrMalformedAccountId(string? id)
        => Assert.Throws<ArgumentException>(() => SessionCredentialSwitch.Validate(Record(), SessionStatus.Running, "10.0.0.5", id));

    [Theory]
    [InlineData(AgentAuthMode.ApiKey)]
    [InlineData(AgentAuthMode.Auto)]
    public void Validate_RejectsSessionsThatDoNotRunOnAProviderFile(AgentAuthMode auth)
        => Assert.Throws<ArgumentException>(() => SessionCredentialSwitch.Validate(Record(auth), SessionStatus.Running, "10.0.0.5", "abcd"));

    [Fact]
    public void Validate_RejectsScheduledSessions()
        => Assert.Throws<ArgumentException>(() =>
            SessionCredentialSwitch.Validate(Record(mode: SessionMode.Scheduled), SessionStatus.Running, "10.0.0.5", "abcd"));

    [Theory]
    [InlineData(SessionStatus.Pending, "10.0.0.5")]
    [InlineData(SessionStatus.Paused, null)]
    [InlineData(SessionStatus.Running, null)]
    public void Validate_RequiresALivePod(string phase, string? podIp)
        => Assert.Throws<InvalidOperationException>(() => SessionCredentialSwitch.Validate(Record(), phase, podIp, "abcd"));

    /// <summary>Changing the account for the next start counts as a runtime change, which a
    /// scheduled session's fixed CronJob spec cannot take.</summary>
    [Fact]
    public void UpdateValidator_TreatsTheAccountAsARuntimeField()
    {
        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.Validate(
            Record(mode: SessionMode.Scheduled), new UpdateSessionRequest { CredentialId = "abcd" }));
        SessionUpdateValidator.Validate(Record(), new UpdateSessionRequest { CredentialId = "abcd" });
    }

    // ------------------------------------------------------------------ controller mapping

    [Fact]
    public async Task Patch_ReturnsTheUpdatedSession()
    {
        var service = new SwitchingSessionService { Result = Info("acct") };

        var result = await Controller(service).SwitchCredential("s1", new SwitchSessionCredentialRequest("acct"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("acct", Assert.IsType<SessionInfo>(ok.Value).CredentialId);
        Assert.Equal(("alice", "s1", "acct"), service.Call);
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException), 404)]
    [InlineData(typeof(ArgumentException), 400)]
    [InlineData(typeof(InvalidOperationException), 409)]
    [InlineData(typeof(HttpRequestException), 502)]
    public async Task Patch_MapsServiceFailuresToStatusCodes(Type exception, int status)
    {
        var service = new SwitchingSessionService { Throw = (Exception)Activator.CreateInstance(exception, "why")! };

        var result = await Controller(service).SwitchCredential("s1", new SwitchSessionCredentialRequest("acct"), CancellationToken.None);

        var actual = result.Result switch
        {
            NotFoundResult => 404,
            ObjectResult o => o.StatusCode,
            _ => -1
        };
        Assert.Equal(status, actual);
    }

    // ------------------------------------------------------------------ the push to the pod

    [Fact]
    public async Task Pusher_PutsTheFileToTheAgentWithTokenAndProvider()
    {
        HttpRequestMessage? captured = null;
        byte[]? body = null;
        var pusher = Pusher(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });

        await pusher.PushAsync("10.0.0.8", "secret-token", AgentKind.OpenClaw, [1, 2, 3], default);

        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.Equal("http://10.0.0.8:7681/agenthub/credentials", captured.RequestUri!.ToString());
        Assert.Equal("secret-token", captured.Headers.GetValues("X-Agent-Token").Single());
        Assert.Equal("openclaw", captured.Headers.GetValues(AgentCredentialPusher.ProviderHeader).Single());
        Assert.Equal([1, 2, 3], body);
    }

    /// <summary>The automatic failover says why it switched; the pod shows that line instead of
    /// the generic one (docs/account-limits.md). Percent-encoded, since a label may hold anything.</summary>
    [Fact]
    public async Task Pusher_CarriesTheSwitchReasonPercentEncoded_AndOmitsTheHeaderWithoutOne()
    {
        var headers = new List<string?>();
        var pusher = Pusher(request =>
        {
            headers.Add(request.Headers.TryGetValues(AgentCredentialPusher.ReasonHeader, out var values) ? values.Single() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });

        await pusher.PushAsync("10.0.0.8", "t", AgentKind.Claude, [1], "Switched to \"Büro\" — limit", default);
        await pusher.PushAsync("10.0.0.8", "t", AgentKind.Claude, [1], default);

        Assert.Equal(Uri.EscapeDataString("Switched to \"Büro\" — limit"), headers[0]);
        Assert.Null(headers[1]);
    }

    [Fact]
    public async Task Pusher_TurnsARefusalIntoAnHttpRequestException()
    {
        var pusher = Pusher(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            pusher.PushAsync("10.0.0.8", "t", AgentKind.Claude, [1], default));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    private static AgentCredentialPusher Pusher(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentHub:AgentPort"] = "7681"
        }).Build();
        return new AgentCredentialPusher(new HttpClient(new DelegatingHandlerStub(handler)), configuration);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }

    private static SessionInfo Info(string credentialId) => new()
    {
        Id = "s1", Title = "t", Owner = "alice", Mode = SessionMode.Interactive, Phase = "Running", CredentialId = credentialId
    };

    private static SessionsController Controller(ISessionService service)
    {
        var controller = new SessionsController(service, null!, []);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"))
            }
        };
        return controller;
    }

    private sealed class SwitchingSessionService : ISessionService
    {
        public SessionInfo? Result { get; init; }
        public Exception? Throw { get; init; }
        public (string Owner, string Id, string CredentialId)? Call { get; private set; }

        public Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId, CancellationToken ct = default)
        {
            Call = (owner, id, credentialId);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result!);
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
