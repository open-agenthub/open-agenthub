using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;
using static AgentHub.Api.Tests.GitOAuthRefreshTests;

namespace AgentHub.Api.Tests;

/// <summary>
/// The renewal lock across replicas (issue #289): two backend instances, each with its own
/// connection pool and the real advisory lock, renew one owner's grant concurrently.
/// </summary>
public sealed class GitOAuthRefreshPostgresTests
{
    [PostgreSqlFact]
    public async Task TwoReplicas_SpendTheRefreshTokenOnce()
    {
        var connection = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        IConfiguration Db() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connection }).Build();
        var store = new MemoryTokenStore();
        var endpoint = new RotatingTokenEndpoint { Delay = TimeSpan.FromMilliseconds(400) };
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        store.Seed("at-0", "rt-0", now.AddMinutes(1));
        GitAuthService Replica() => new(Config(), new Factory(endpoint), new RecordingLogger<GitAuthService>(), store,
            new PostgresProviderLoginLock(Db()), new GitOAuthRefreshOptions { LockWait = TimeSpan.FromSeconds(15) },
            new ManualTime(now));
        var replicas = new[] { Replica(), Replica() };
        RepoRef[] repos = [new() { Url = "https://gitlab.example.com/group/project.git", ProviderId = "gitlab" }];

        var stores = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Task.Run(() => replicas[i % 2].BuildCredentialStoreAsync("owner@example.com", repos))));

        Assert.Equal(1, endpoint.Refreshes);
        Assert.Equal(0, endpoint.InvalidGrants);
        Assert.All(stores, s => Assert.Equal("https://oauth2:at-1@gitlab.example.com\n", s));
        Assert.Equal("rt-1", store.Get("gitlab.refresh"));
    }
}
