using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Otel;
using AgentHub.Api.Persistence;
using AgentHub.Api.Usage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>POST /internal/sessions/{id}/resources — the pod resource snapshot ingest.</summary>
public class InternalResourcesTests
{
    private const string SessionId = "sess-1";
    private const string Token = "callback-token";
    private const string Owner = "alice";

    [Fact]
    public async Task Resources_WithMatchingToken_StoresSampleForSessionOwner()
    {
        var usage = new RecordingUsageStore();
        var controller = Controller(usage);

        var result = await controller.Resources(SessionId,
            new InternalController.ResourcesBody(12.5, 200_000_000, 1_000, 2_000), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var (sid, owner, sample) = Assert.Single(usage.Samples);
        Assert.Equal(SessionId, sid);
        Assert.Equal(Owner, owner);
        Assert.Equal(new SessionResourceSample(12.5, 200_000_000, 1_000, 2_000), sample);
    }

    [Fact]
    public async Task Resources_WithWrongToken_ReturnsUnauthorized()
    {
        var usage = new RecordingUsageStore();
        var controller = Controller(usage, token: "wrong");

        var result = await controller.Resources(SessionId,
            new InternalController.ResourcesBody(1, 1, 1, 1), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(usage.Samples);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    [InlineData(double.NaN, 0, 0, 0)]
    public async Task Resources_WithInvalidCounters_ReturnsBadRequest(double cpu, long mem, long rx, long tx)
    {
        var usage = new RecordingUsageStore();
        var controller = Controller(usage);

        var result = await controller.Resources(SessionId,
            new InternalController.ResourcesBody(cpu, mem, rx, tx), CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        Assert.Empty(usage.Samples);
    }

    [Fact]
    public async Task Resources_WithoutUsageStore_IsANoOp()
    {
        var controller = Controller(usage: null);

        var result = await controller.Resources(SessionId,
            new InternalController.ResourcesBody(1, 1, 1, 1), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    // ------------------------------------------------- POST /internal/sessions/{id}/usage

    private static readonly AgentUsageReport Report = new(400, 50, 600, 0, 20, 1, null);

    [Theory]
    [InlineData(AgentAuthMode.Subscription, "subscription")]
    [InlineData(AgentAuthMode.ApiKey, "api")]
    [InlineData(AgentAuthMode.CustomEndpoint, "custom")]
    public async Task Usage_TakesOwnerAndBillingFromTheSessionRecord(AgentAuthMode authMode, string billing)
    {
        var usage = new RecordingUsageStore();
        var controller = Controller(usage, agent: AgentKind.Codex, authMode: authMode);

        var result = await controller.Usage(SessionId, Report, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var reported = Assert.Single(usage.Reports);
        Assert.Equal((SessionId, Owner, "codex", billing, Report), reported);
    }

    [Fact]
    public async Task Usage_WithWrongToken_ReturnsUnauthorized()
    {
        var usage = new RecordingUsageStore();
        var controller = Controller(usage, token: "wrong", agent: AgentKind.Codex);

        Assert.IsType<UnauthorizedResult>(await controller.Usage(SessionId, Report, CancellationToken.None));
        Assert.Empty(usage.Reports);
    }

    [Fact]
    public async Task Usage_ForClaude_IsIgnoredBecauseItsTokensArriveOverOtlp()
    {
        var usage = new RecordingUsageStore();
        var controller = Controller(usage, agent: AgentKind.Claude, authMode: AgentAuthMode.Subscription);

        Assert.IsType<NoContentResult>(await controller.Usage(SessionId, Report, CancellationToken.None));
        Assert.Empty(usage.Reports);
    }

    [Fact]
    public async Task Usage_WithNegativeOrNonFiniteValues_ReturnsBadRequest()
    {
        var usage = new RecordingUsageStore();
        var controller = Controller(usage, agent: AgentKind.OpenCode);

        Assert.IsType<BadRequestResult>(await controller.Usage(SessionId, Report with { OutputTokens = -1 }, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await controller.Usage(SessionId, Report with { CostUsd = double.NaN }, CancellationToken.None));
        Assert.Empty(usage.Reports);
    }

    // ------------------------------------------------------------------ fixtures

    private static InternalController Controller(RecordingUsageStore? usage, string token = Token,
        AgentKind agent = AgentKind.Claude, AgentAuthMode authMode = AgentAuthMode.Auto)
    {
        var session = new SessionRecord
        {
            Id = SessionId,
            Owner = Owner,
            CallbackToken = Token,
            Mode = SessionMode.Autonomous,
            Agent = agent,
            AuthMode = authMode,
            Title = "session"
        };
        var controller = new InternalController(
            new SingleSessionStore(session), [], null!, null!, [], [], null!, null!,
            usage: usage)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Agent-Token"] = token;
        return controller;
    }

    private sealed class RecordingUsageStore : IUsageStore
    {
        public List<(string SessionId, string Owner, SessionResourceSample Sample)> Samples { get; } = [];
        public List<(string SessionId, string Owner, string Source, string Billing, AgentUsageReport Report)> Reports { get; } = [];

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AddDeltaAsync(SessionUsageDelta delta, CancellationToken ct = default) => Task.FromResult(true);
        public Task AddReportedUsageAsync(string sessionId, string owner, string source, string billing,
            AgentUsageReport report, CancellationToken ct = default)
        {
            Reports.Add((sessionId, owner, source, billing, report));
            return Task.CompletedTask;
        }
        public Task AddResourceSampleAsync(string sessionId, string owner, SessionResourceSample sample, CancellationToken ct = default)
        {
            Samples.Add((sessionId, owner, sample));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SessionUsage>> ListByOwnerAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionUsage>>([]);
        public Task<SessionUsage?> GetAsync(string owner, string sessionId, CancellationToken ct = default)
            => Task.FromResult<SessionUsage?>(null);
        public Task<UsageSummary> SummaryAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default)
            => Task.FromResult(new UsageSummary { Owner = owner });
        public Task<double> MonthToDateApiCostAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(0d);
    }

    internal sealed class SingleSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(owner == session.Owner && id == session.Id ? session : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id == session.Id ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
