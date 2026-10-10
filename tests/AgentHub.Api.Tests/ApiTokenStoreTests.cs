using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Personal API tokens: only a hash is stored, and a token's credential restriction survives the
/// round trip from the settings page to the remote resolver (docs/credential-scopes.md).
/// </summary>
public sealed class ApiTokenStoreTests
{
    [Fact]
    public void Hash_IsLowercaseSha256OfTheToken_SoTheDatabaseNeverHoldsIt()
    {
        // Known digest of "oah_x" (sha256sum): what a stored row holds and what a lookup computes.
        Assert.Equal("2ff79d155f41027763cb3a76740bb2344f0d2ddd70ee75776c708e0bd428e8de", ApiTokenStore.Hash("oah_x"));
        Assert.Equal(64, ApiTokenStore.Hash("anything").Length);
        Assert.NotEqual(ApiTokenStore.Hash("oah_a"), ApiTokenStore.Hash("oah_b"));
    }

    [PostgreSqlFact]
    public async Task Scope_RoundTripsThroughCreateListResolveAndUpdate()
    {
        await using var db = await TokenDatabase.CreateAsync();
        var scope = new ApiTokenScope
        {
            ProviderAccounts = new() { ["Claude"] = ["work0001"] }, GitPats = ["pat-a"], ApiKeys = true
        };

        var created = await db.Tokens.CreateAsync("alice", "ci", "oah_secret1", scope);
        var listed = Assert.Single(await db.Tokens.ListByOwnerAsync("alice"));
        var caller = await db.Tokens.FindCallerByTokenAsync("oah_secret1");

        Assert.Equal(created.Id, listed.Id);
        Assert.Equal(["work0001"], listed.AllowedCredentials!.ProviderAccounts!["Claude"]);
        Assert.Equal("alice", caller!.Owner);
        Assert.Equal(["pat-a"], caller.Scope!.GitPats);
        Assert.True(caller.Scope.AllowsApiKeys);
        Assert.NotNull((await db.Tokens.ListByOwnerAsync("alice")).Single().LastUsedAt);

        // Lifting the restriction, and a stranger cannot.
        Assert.False(await db.Tokens.UpdateScopeAsync("bob", created.Id, null));
        Assert.True(await db.Tokens.UpdateScopeAsync("alice", created.Id, null));
        Assert.Null((await db.Tokens.FindCallerByTokenAsync("oah_secret1"))!.Scope);
        Assert.Null(await db.Tokens.FindCallerByTokenAsync("oah_unknown"));
        // The plaintext is nowhere in the table.
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM api_tokens WHERE token_hash LIKE '%secret1%'"));
    }

    [PostgreSqlFact]
    public async Task ATokenFromBeforeScopes_IsUnrestricted()
    {
        await using var db = await TokenDatabase.CreateAsync();
        await db.Tokens.CreateAsync("alice", "old", "oah_old");
        await db.ExecAsync("UPDATE api_tokens SET allowed_credentials = NULL");

        var caller = await db.Tokens.FindCallerByTokenAsync("oah_old");

        Assert.Null(caller!.Scope);
        Assert.Null(Assert.Single(await db.Tokens.ListByOwnerAsync("alice")).AllowedCredentials);
    }

    [PostgreSqlFact]
    public async Task Controller_ValidatesTheScopeAgainstTheCallersCredentials_AndStoresIt()
    {
        await using var db = await TokenDatabase.CreateAsync();
        var controller = Controller(db.Tokens, "alice");

        var bad = await controller.Create(new ApiTokensController.CreateTokenRequest("ci",
            new ApiTokenScope { ProviderAccounts = new() { ["Claude"] = ["nope0000"] } }), CancellationToken.None);
        Assert.Contains("nope0000", Assert.IsType<BadRequestObjectResult>(bad.Result).Value?.ToString());

        var ok = await controller.Create(new ApiTokensController.CreateTokenRequest("ci",
            new ApiTokenScope { ProviderAccounts = new() { ["claude"] = ["work0001"] }, GitPats = ["*"] }), CancellationToken.None);
        var token = Assert.IsType<ApiTokensController.CreatedToken>(Assert.IsType<OkObjectResult>(ok.Result).Value);
        Assert.StartsWith("oah_", token.Token);
        Assert.Equal(["work0001"], token.AllowedCredentials!.ProviderAccounts!["Claude"]);

        var resolved = await db.Tokens.FindCallerByTokenAsync(token.Token);
        Assert.True(resolved!.Scope!.AllowsAllGitPats);

        Assert.IsType<NoContentResult>(await controller.Update(token.Id,
            new ApiTokensController.UpdateTokenRequest(new ApiTokenScope { GitPats = ["pat-a"] }), CancellationToken.None));
        Assert.Equal(["pat-a"], (await db.Tokens.FindCallerByTokenAsync(token.Token))!.Scope!.GitPats);
        Assert.IsType<NotFoundResult>(await Controller(db.Tokens, "bob").Update(token.Id,
            new ApiTokensController.UpdateTokenRequest(null), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.Update(token.Id,
            new ApiTokensController.UpdateTokenRequest(null), CancellationToken.None));
        Assert.Null((await db.Tokens.FindCallerByTokenAsync(token.Token))!.Scope);
    }

    // ------------------------------------------------------------------ fixtures

    private static ApiTokensController Controller(ApiTokenStore store, string user)
    {
        var controller = new ApiTokensController(store, new CredentialSessions())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test"))
                }
            }
        };
        return controller;
    }

    private sealed class CredentialSessions : ISessionService
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>>(
                new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>
                {
                    ["Claude"] = [new("work0001", "Work", null, null, DateTime.UtcNow, null, true)]
                });
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(new CredentialStatus { GitPats = [new GitPatInfo("pat-a", "gitlab", "gitlab.example.com")] });

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
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

    /// <summary>Its own schema per test, like the other Postgres fixtures, so runs cannot see each other's rows.</summary>
    private sealed class TokenDatabase : IAsyncDisposable
    {
        private readonly string _base;
        private readonly string _schema;
        private readonly NpgsqlDataSource _raw;

        private TokenDatabase(string @base, string schema, ApiTokenStore tokens, string connectionString)
        {
            _base = @base;
            _schema = schema;
            Tokens = tokens;
            _raw = NpgsqlDataSource.Create(connectionString);
        }

        public ApiTokenStore Tokens { get; }

        public static async Task<TokenDatabase> CreateAsync()
        {
            var @base = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)
                ?? throw new InvalidOperationException("connection string required");
            var schema = $"tokens_test_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(@base))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
                await command.ExecuteNonQueryAsync();
            }
            var connectionString = new NpgsqlConnectionStringBuilder(@base) { SearchPath = schema }.ConnectionString;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            }).Build();
            var tokens = new ApiTokenStore(config);
            await tokens.InitializeAsync();
            return new TokenDatabase(@base, schema, tokens, connectionString);
        }

        public async Task ExecAsync(string sql)
        {
            await using var cmd = _raw.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var cmd = _raw.CreateCommand(sql);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await _raw.DisposeAsync();
            await using var connection = new NpgsqlConnection(_base);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
