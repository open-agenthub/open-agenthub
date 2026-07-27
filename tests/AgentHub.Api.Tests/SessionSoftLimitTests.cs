using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionSoftLimitTests
{
    [Fact]
    public async Task EnsureCanCreate_Throws_WhenRunningCountAtMax()
    {
        var store = new FakeStore(
            Rec("1", "Pending"),
            Rec("2", "Running"),
            Rec("3", "Paused"),
            Rec("4", "Scheduled"));

        var ex = await Assert.ThrowsAsync<SessionLimitExceededException>(() =>
            SessionSoftLimit.EnsureCanCreateAsync(store, "alice", maxRunning: 4));

        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public async Task EnsureCanCreate_Allows_WhenUnderMax()
    {
        var store = new FakeStore(
            Rec("1", "Pending"),
            Rec("2", "Succeeded"),
            Rec("3", "Failed"));

        await SessionSoftLimit.EnsureCanCreateAsync(store, "alice", maxRunning: 2);
    }

    [Fact]
    public async Task EnsureCanCreate_Ignores_SucceededAndFailed()
    {
        var store = new FakeStore(
            Rec("1", "Succeeded"),
            Rec("2", "Failed"),
            Rec("3", "Succeeded"));

        await SessionSoftLimit.EnsureCanCreateAsync(store, "alice", maxRunning: 1);
    }

    [Fact]
    public async Task EnsureCanCreate_Throws_WhenCountExceedsMax()
    {
        var store = new FakeStore(Rec("1", "Running"), Rec("2", "Pending"));

        await Assert.ThrowsAsync<SessionLimitExceededException>(() =>
            SessionSoftLimit.EnsureCanCreateAsync(store, "alice", maxRunning: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NormalizeMax_RequiresPositive_DefaultsTo20(int configured)
    {
        Assert.Equal(20, SessionSoftLimit.NormalizeMax(configured));
    }

    private static SessionRecord Rec(string id, string status) => new()
    {
        Id = id,
        Owner = "alice",
        CallbackToken = $"tok-{id}",
        Status = status,
        Mode = SessionMode.Autonomous
    };

    private sealed class FakeStore(params SessionRecord[] sessions) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(sessions.FirstOrDefault(s => s.Owner == owner && s.Id == id));
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(sessions.FirstOrDefault(s => s.CallbackToken == token));
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>(sessions.Where(s => s.Owner == owner).ToList());
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
