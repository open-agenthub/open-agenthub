using System.Reflection;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionStorageCleanupTests
{
    [Fact]
    public async Task DeleteCoversBothEncodingsAndPropagatesFailure()
    {
        var storage = new Storage();
        await SessionStorageCleanup.DeleteAsync(storage, "alice/team", "abc", default);
        Assert.Equal(new[] { $"sessions/{KubernetesSessionService.OwnerKey("alice/team")}/abc/", "sessions/alice%2Fteam/abc/" }, storage.Deleted);
        storage.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => SessionStorageCleanup.DeleteAsync(storage, "alice", "abc", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionStorageCleanup.DeleteAsync(new NullArtifactStore(), "alice", "abc", default));
    }

    [Fact]
    public async Task DryRunIsReadOnly_ApplyRechecksAllOwners_AndIsIdempotent()
    {
        var storage = new Storage { Prefixes = ["sessions/owner/live/", "sessions/owner/gone/", "sessions/other/new/", "skills/skill/"] };
        var registry = DispatchProxy.Create<ISessionStore, Registry>();
        var records = (Registry)registry;
        records.Existing.Add("live");
        var cleanup = Create(storage, registry, new Locks());
        var preview = await cleanup.PreviewAsync("admin", default);
        Assert.Empty(storage.Deleted);
        Assert.Equal(["sessions/owner/gone/", "sessions/other/new/"], preview.Prefixes);
        records.Existing.Add("new");
        var result = await cleanup.ApplyAsync("admin", preview.Token, default);
        Assert.Equal(new SessionStorageCleanup.Result(2, 1, 1, 0), result);
        Assert.Equal(["sessions/owner/gone/"], storage.Deleted);
        await cleanup.ApplyAsync("admin", preview.Token, default);
        Assert.Equal(2, storage.Deleted.Count);
    }

    [Fact]
    public async Task PlansCannotBeForgedOrTransferredToAnotherAdmin()
    {
        var cleanup = Create(new Storage { Prefixes = ["sessions/owner/gone/"] }, DispatchProxy.Create<ISessionStore, Registry>(), new Locks());
        var plan = await cleanup.PreviewAsync("admin", default);
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => cleanup.ApplyAsync("another", plan.Token, default));
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => cleanup.ApplyAsync("admin", "invalid", default));
    }

    [Fact]
    public async Task LockedSessionsAreSkippedAndFailuresAreCounted()
    {
        var storage = new Storage { Prefixes = ["sessions/owner/locked/", "sessions/owner/gone/"], Fail = true };
        var cleanup = Create(storage, DispatchProxy.Create<ISessionStore, Registry>(), new Locks { Busy = "locked" });
        var plan = await cleanup.PreviewAsync("admin", default);
        Assert.Equal(new SessionStorageCleanup.Result(2, 0, 1, 1), await cleanup.ApplyAsync("admin", plan.Token, default));
    }

    [Theory]
    [InlineData("skills/owner/id/")]
    [InlineData("sessions/owner/")]
    [InlineData("sessions//id/")]
    [InlineData("sessions/owner/id/artifacts/")]
    [InlineData("sessions/owner/%2E%2E/")]
    [InlineData("sessions/owner/a%2Fb/")]
    public void RefusesOtherOrAmbiguousPrefixes(string prefix) => Assert.Null(SessionStorageCleanup.SessionId(prefix));

    [Fact]
    public async Task ExpiredPlansRequireANewDryRun()
    {
        var protection = new EphemeralDataProtectionProvider();
        var token = protection.CreateProtector("session-storage-cleanup-v1", "admin").Protect(
            System.Text.Json.JsonSerializer.Serialize(new SessionStorageCleanup.Plan(DateTime.UtcNow.AddHours(-1), ["sessions/owner/gone/"])));
        var cleanup = new SessionStorageCleanup(new Storage(), DispatchProxy.Create<ISessionStore, Registry>(), new Locks(), protection, NullLogger<SessionStorageCleanup>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => cleanup.ApplyAsync("admin", token, default));
    }

    private static SessionStorageCleanup Create(IArtifactStore storage, ISessionStore registry, ISessionExpiryLock locks)
        => new(storage, registry, locks, new EphemeralDataProtectionProvider(), NullLogger<SessionStorageCleanup>.Instance);

    public class Registry : DispatchProxy
    {
        public HashSet<string> Existing { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISessionStore.GetByIdAsync))
            {
                var id = (string)args![0]!;
                return Task.FromResult(Existing.Contains(id) ? new SessionRecord { Id = id, Owner = "different-owner", CallbackToken = "token" } : null);
            }
            throw new NotSupportedException(method.Name);
        }
    }

    public sealed class Locks : ISessionExpiryLock
    {
        public string? Busy { get; init; }
        public Task<IAsyncDisposable?> TryAcquireAsync(string id, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(id == Busy ? null : new Held());
        private sealed class Held : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class Storage : IArtifactStore
    {
        public string[] Prefixes { get; init; } = [];
        public List<string> Deleted { get; } = [];
        public bool Fail { get; set; }
        public string PresignPut(string key, TimeSpan ttl) => "";
        public string PresignGet(string key, TimeSpan ttl) => "";
        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix, CancellationToken ct = default)
        {
            if (Fail) throw new IOException("Storage unavailable.");
            Deleted.Add(prefix);
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<string> ListSessionPrefixesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var prefix in Prefixes) yield return prefix;
        }
    }
}
