using System.Runtime.CompilerServices;
using AgentHub.Api.Admin;
using AgentHub.Api.Auth;
using AgentHub.Api.Chat;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The owner handover (#501, docs/apikey-handover.md) against real Postgres: the real stores create
/// their tables, rows are written the way the stores write them, and the switch runs in the real
/// transaction.
/// </summary>
public class OwnerHandoverPostgresTests
{
    private const string From = "admin";
    private const string To = "alice";

    [PostgreSqlFact]
    public async Task Apply_moves_every_owned_row_renames_clashes_and_records_the_audit()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();

        var store = db.Store();
        var inventory = await store.InventoryAsync(From, To);
        Assert.Equal(3, inventory.Moves[OwnerHandoverStore.Sessions]);
        Assert.Equal(2, inventory.Moves[OwnerHandoverStore.Projects]);
        Assert.Equal(1, inventory.Stays[OwnerHandoverStore.ApiTokens]);
        Assert.Equal(1, inventory.Stays[OwnerHandoverStore.ChatThreads]);
        Assert.Contains(inventory.Renames, r => r is { Kind: OwnerHandoverStore.Projects, Name: "Tools", NewName: "Tools (API key)" });
        Assert.Contains(inventory.Renames, r => r is { Kind: OwnerHandoverStore.Skills, Name: "deploy", NewName: "deploy-api-key" });
        Assert.Contains(inventory.Renames, r => r is { Kind: OwnerHandoverStore.McpServers, Name: "github", NewName: "github-api-key" });

        var audit = await store.ApplyAsync("root", From, To, credentials: false, inventory.Moves);

        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'admin'"));
        Assert.Equal(4, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'alice'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'bob'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM projects WHERE owner = 'alice' AND name = 'Tools (API key)'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM projects WHERE owner = 'alice' AND name = 'tools'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM skills WHERE owner = 'alice' AND name = 'deploy-api-key'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM mcp_servers WHERE owner = 'alice' AND name = 'github-api-key'"));
        // Monthly spend adds up instead of colliding on (owner, month).
        Assert.Equal(5.0, await db.ScalarAsync<double>("SELECT cost_usd FROM usage_monthly WHERE owner = 'alice' AND month = date '2026-10-01'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM usage_monthly WHERE owner = 'admin'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM session_usage WHERE owner = 'alice' AND session_id = 's1'"));
        // Object keys that carry the owner point at the target's prefixes the service copied to.
        Assert.Equal("sessions/alice/s1/files/f1/a.txt", await db.ScalarAsync<string>("SELECT storage_locator FROM session_files WHERE id = 'f1'"));
        var key = KubernetesSessionService.OwnerKey(To);
        Assert.Equal($"sessions/{key}/s1/transcript.jsonl", await db.ScalarAsync<string>("SELECT transcript_key FROM session_executions WHERE session_id = 's1'"));
        Assert.Equal($"sessions/{key}/s1/claude-state.tgz", await db.ScalarAsync<string>("SELECT state_key FROM session_executions WHERE session_id = 's1'"));
        // Shares: the target does not keep a share of what it now owns; bob's share to the old
        // owner becomes the target's; a share bob gave to both stays once.
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM session_shares WHERE session_id = 's1' AND recipient_owner = 'alice'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM session_shares WHERE session_id = 's1' AND recipient_owner = 'carol'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM session_shares WHERE session_id = 'sb' AND recipient_owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM session_shares WHERE recipient_owner = 'admin'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM project_shares WHERE project_id = 'pb' AND subject = 'alice'"));
        // Chat threads of moved sessions end; API tokens stay with the old name.
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_session_bindings WHERE session_id = 's1'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM api_tokens WHERE owner = 'admin'"));

        Assert.Equal("committed", audit.State);
        var history = await store.ListAsync(10);
        var row = Assert.Single(history);
        Assert.Equal(("root", From, To, false), (row.Admin, row.From, row.To, row.Credentials));
        Assert.Equal(3, row.Counts[OwnerHandoverStore.Sessions]);
        Assert.Equal(["s1", "s2", "s3"], row.SessionIds.Order());
        Assert.Equal(["s3"], row.ScheduledIds);
    }

    [PostgreSqlFact]
    public async Task A_failure_part_way_through_the_switch_changes_nothing()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var before = await db.SnapshotAsync();

        // Fails after sessions, projects and shares were already updated inside the transaction.
        var store = db.Store((table, _) => table == "webhook_triggers"
            ? throw new IOException("connection lost") : Task.CompletedTask);
        var inventory = await store.InventoryAsync(From, To);
        await Assert.ThrowsAsync<IOException>(() => store.ApplyAsync("root", From, To, false, inventory.Moves));

        Assert.Equal(before, await db.SnapshotAsync());
        Assert.Empty(await db.Store().ListAsync(10));
    }

    [PostgreSqlFact]
    public async Task A_failure_at_the_audit_row_rolls_the_switch_back()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var before = await db.SnapshotAsync();
        var store = db.Store((table, _) => table == "owner_handovers" ? throw new IOException("disk full") : Task.CompletedTask);
        var inventory = await store.InventoryAsync(From, To);
        await Assert.ThrowsAsync<IOException>(() => store.ApplyAsync("root", From, To, false, inventory.Moves));
        Assert.Equal(before, await db.SnapshotAsync());
    }

    [PostgreSqlFact]
    public async Task Apply_refuses_counts_that_changed_since_the_preview()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var store = db.Store();
        var inventory = await store.InventoryAsync(From, To);
        await db.ExecAsync("""
            INSERT INTO sessions (id, owner, mode, claude_session_id, callback_token, agent_session_id)
            VALUES ('s9', 'admin', 'Interactive', 'c9', 't9', 'c9')
            """);
        await Assert.ThrowsAsync<HandoverStaleException>(() => store.ApplyAsync("root", From, To, false, inventory.Moves));
        Assert.Equal(4, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'admin'"));
    }

    [PostgreSqlFact]
    public async Task A_second_apply_finds_nothing_left()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var store = db.Store();
        await store.ApplyAsync("root", From, To, false, (await store.InventoryAsync(From, To)).Moves);
        var again = await store.InventoryAsync(From, To);
        Assert.All(again.Moves.Values, v => Assert.Equal(0, v));
        Assert.Empty(again.Renames);
    }

    // ---- service, with the cluster faked and storage in memory ------------------------------

    [PostgreSqlFact]
    public async Task Without_the_credentials_tick_secrets_stay_and_objects_move()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var runtime = new FakeRuntime();
        var storage = new MemoryStorage();
        var oldKey = KubernetesSessionService.OwnerKey(From);
        storage.Put($"sessions/{oldKey}/s1/claude-state.tgz", 300);
        storage.Put($"sessions/{oldKey}/s1/transcript.jsonl", 20);
        storage.Put("sessions/admin/s1/files/f1/a.txt", 5);
        storage.Put($"sessions/{KubernetesSessionService.OwnerKey("bob")}/sb/claude-state.tgz", 7);
        var service = db.Service(runtime, storage);

        var preview = await service.PreviewAsync("root", From, To, includeCredentials: false, default);
        Assert.Equal(new HandoverStorage(true, 3, 325), preview.Storage);
        Assert.Equal(1, preview.ScheduledSessions);
        Assert.NotNull(preview.Token);

        var result = await service.ApplyAsync("root", preview.Token!, default);

        Assert.False(result.CleanupPending);
        Assert.Equal(0, runtime.Copies);
        Assert.Equal(0, runtime.Deletes);
        var newKey = KubernetesSessionService.OwnerKey(To);
        Assert.Equal(
            new[] { $"sessions/{KubernetesSessionService.OwnerKey("bob")}/sb/claude-state.tgz", $"sessions/{newKey}/s1/claude-state.tgz",
                $"sessions/{newKey}/s1/transcript.jsonl", "sessions/alice/s1/files/f1/a.txt" }.Order(StringComparer.Ordinal),
            storage.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["suspend:s3", "rebuild:alice:s3"], runtime.Calls);
        Assert.Equal("completed", (await db.Store().ListAsync(1))[0].State);
    }

    [PostgreSqlFact]
    public async Task With_the_credentials_tick_secrets_are_copied_before_and_removed_after()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var runtime = new FakeRuntime();
        var service = db.Service(runtime, new MemoryStorage());
        var preview = await service.PreviewAsync("root", From, To, includeCredentials: true, default);
        await service.ApplyAsync("root", preview.Token!, default);
        Assert.Equal(["suspend:s3", "copy:admin->alice", "rebuild:alice:s3", "delete:admin"], runtime.Calls);
        Assert.True((await db.Store().ListAsync(1))[0].Credentials);
    }

    [PostgreSqlFact]
    public async Task A_running_session_blocks_the_handover()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var runtime = new FakeRuntime { Live = ["s1"] };
        var service = db.Service(runtime, new MemoryStorage());
        var preview = await service.PreviewAsync("root", From, To, false, default);
        Assert.Null(preview.Token);
        Assert.Equal(["s1"], preview.LiveSessions);

        runtime.Live = [];
        var token = (await service.PreviewAsync("root", From, To, false, default)).Token!;
        runtime.Live = ["s2"];
        var refused = await Assert.ThrowsAsync<HandoverRefusedException>(() => service.ApplyAsync("root", token, default));
        Assert.Equal(("sessions_running", 409), (refused.Code, refused.Status));
        Assert.Equal(3, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'admin'"));
    }

    [PostgreSqlFact]
    public async Task A_failed_copy_changes_no_ownership_and_restores_the_schedules()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var runtime = new FakeRuntime();
        var storage = new MemoryStorage { FailCopy = true };
        storage.Put($"sessions/{KubernetesSessionService.OwnerKey(From)}/s1/claude-state.tgz", 300);
        var service = db.Service(runtime, storage);
        var token = (await service.PreviewAsync("root", From, To, true, default)).Token!;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync("root", token, default));

        Assert.Equal(3, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'admin'"));
        Assert.Empty(await db.Store().ListAsync(10));
        Assert.Equal(["suspend:s3", "copy:admin->alice", "restore:s3"], runtime.Calls);

        // The rerun copies what is left and completes.
        storage.FailCopy = false;
        token = (await service.PreviewAsync("root", From, To, true, default)).Token!;
        Assert.False((await service.ApplyAsync("root", token, default)).CleanupPending);
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'admin'"));
    }

    [PostgreSqlFact]
    public async Task A_failed_cleanup_is_finished_by_the_next_call()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var runtime = new FakeRuntime { FailRebuild = true };
        var service = db.Service(runtime, new MemoryStorage());
        var token = (await service.PreviewAsync("root", From, To, false, default)).Token!;
        var result = await service.ApplyAsync("root", token, default);
        Assert.True(result.CleanupPending);
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'admin'"));
        Assert.Equal("committed", (await db.Store().ListAsync(1))[0].State);

        runtime.FailRebuild = false;
        await service.FinishPendingAsync(default);
        Assert.Equal("completed", (await db.Store().ListAsync(1))[0].State);
    }

    [PostgreSqlFact]
    public async Task The_token_is_bound_to_the_admin_who_previewed()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var service = db.Service(new FakeRuntime(), new MemoryStorage());
        var token = (await service.PreviewAsync("root", From, To, false, default)).Token!;
        var refused = await Assert.ThrowsAsync<HandoverRefusedException>(() => service.ApplyAsync("mallory", token, default));
        Assert.Equal("invalid_token", refused.Code);
    }

    [PostgreSqlFact]
    public async Task Source_and_target_are_checked()
    {
        await using var db = await HandoverDatabase.CreateAsync();
        await db.SeedAsync();
        var service = db.Service(new FakeRuntime(), new MemoryStorage());
        async Task<string> Code(string from, string to) =>
            (await Assert.ThrowsAsync<HandoverRefusedException>(() => service.PreviewAsync("root", from, to, false, default))).Code;

        Assert.Equal("invalid_source", await Code("bob", To));          // an OIDC user is not a source
        Assert.Equal("invalid_target", await Code(From, "dev"));        // nor is a single-user name a target
        Assert.Equal("unknown_target", await Code(From, "nobody"));     // the target has signed in
        Assert.Equal("unknown_target", await Code(From, "ALICE"));      // by exact name

        var apiKeyMode = db.Service(new FakeRuntime(), new MemoryStorage(), HubAuthOptions.ForApiKey("k", From));
        Assert.Equal("oidc_required",
            (await Assert.ThrowsAsync<HandoverRefusedException>(() => apiKeyMode.PreviewAsync("root", From, To, false, default))).Code);
    }

    private sealed class FakeRuntime : IOwnerHandoverRuntime
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<string> Live { get; set; } = [];
        public bool FailRebuild { get; set; }
        public int Copies => Calls.Count(c => c.StartsWith("copy:"));
        public int Deletes => Calls.Count(c => c.StartsWith("delete:"));

        public Task<IReadOnlyList<string>> LiveSessionIdsAsync(string owner, CancellationToken ct = default) => Task.FromResult(Live);
        public Task<IReadOnlyList<CredentialKindPreview>> CredentialPreviewAsync(string from, string to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CredentialKindPreview>>([new(OwnerHandoverCredentials.GitPats, 2, 0)]);
        public Task CopyCredentialsAsync(string from, string to, CancellationToken ct = default) { Calls.Add($"copy:{from}->{to}"); return Task.CompletedTask; }
        public Task DeleteCredentialsAsync(string owner, CancellationToken ct = default) { Calls.Add($"delete:{owner}"); return Task.CompletedTask; }
        public Task SuspendSchedulesAsync(IReadOnlyCollection<string> ids, bool suspend, CancellationToken ct = default)
        { Calls.AddRange(ids.Select(id => $"{(suspend ? "suspend" : "restore")}:{id}")); return Task.CompletedTask; }
        public Task RebuildScheduleAsync(string owner, string id, CancellationToken ct = default)
        {
            if (FailRebuild) throw new InvalidOperationException("apiserver unavailable");
            Calls.Add($"rebuild:{owner}:{id}");
            return Task.CompletedTask;
        }
    }

    internal sealed class MemoryStorage : IArtifactStore
    {
        private readonly SortedDictionary<string, long> _objects = new(StringComparer.Ordinal);
        public bool FailCopy { get; set; }
        public IEnumerable<string> Keys => _objects.Keys;
        public void Put(string key, long size) => _objects[key] = size;
        public string PresignPut(string key, TimeSpan ttl) => "";
        public string PresignGet(string key, TimeSpan ttl) => "";
        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key, CancellationToken ct = default) { _objects.Remove(key); return Task.CompletedTask; }
        public Task DeleteByPrefixAsync(string prefix, CancellationToken ct = default)
        {
            foreach (var key in _objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) _objects.Remove(key);
            return Task.CompletedTask;
        }
        public Task<ArtifactObjectInfo?> HeadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_objects.TryGetValue(key, out var size) ? new ArtifactObjectInfo(size, null) : null);
        public async IAsyncEnumerable<ArtifactListing> ListObjectsAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var (key, size) in _objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                yield return new ArtifactListing(key, size);
        }
        public Task CopyObjectAsync(string sourceKey, string destinationKey, long size, CancellationToken ct = default)
        {
            if (FailCopy) throw new IOException("object storage unavailable");
            _objects[destinationKey] = _objects[sourceKey];
            return Task.CompletedTask;
        }
    }

    private sealed class HandoverDatabase : IAsyncDisposable
    {
        private readonly string _base;
        private readonly string _schema;
        private readonly NpgsqlDataSource _raw;
        public IConfiguration Config { get; }

        private HandoverDatabase(string @base, string schema, IConfiguration config)
        {
            _base = @base; _schema = schema; Config = config;
            _raw = NpgsqlDataSource.Create(config.GetConnectionString("Postgres")!);
        }

        public OwnerHandoverStore Store(Func<string, CancellationToken, Task>? beforeStep = null) => new(Config, beforeStep);

        public OwnerHandoverService Service(IOwnerHandoverRuntime runtime, IArtifactStore storage, HubAuthOptions? auth = null) =>
            new(Store(), new SingleUserOwnerStore(Config), new UserDirectory(Config), storage,
                new EphemeralDataProtectionProvider(), auth ?? HubAuthOptions.Resolve(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Oidc:Authority"] = "https://sso.example.com" }).Build()),
                NullLogger<OwnerHandoverService>.Instance, runtime);

        public static async Task<HandoverDatabase> CreateAsync()
        {
            var baseCs = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
            var schema = $"handover_test_{Guid.NewGuid():N}";
            await using (var c = new NpgsqlConnection(baseCs))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", c);
                await cmd.ExecuteNonQueryAsync();
            }
            var cs = new NpgsqlConnectionStringBuilder(baseCs) { SearchPath = schema, Pooling = false }.ConnectionString;
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = cs }).Build();
            var protector = new Library.McpSecretProtector(new EphemeralDataProtectionProvider());
            await new UserDirectory(config).InitializeAsync();
            await new PostgresSessionStore(config).InitializeAsync();
            await new PostgresProjectStore(config).InitializeAsync();
            await new PostgresUsageStore(config).InitializeAsync();
            await new ApiTokenStore(config).InitializeAsync();
            await new ChatBindingStore(config).InitializeAsync();
            await new Library.McpServerStore(config, protector).InitializeAsync();
            await new Library.SkillStore(config, new MemoryStorage(), NullLogger<Library.SkillStore>.Instance).InitializeAsync();
            await new LibraryShareStore(config).InitializeAsync();
            await new SessionShareStore(config, NullLogger<SessionShareStore>.Instance).InitializeAsync();
            await new PostgresSessionFileRegistry(config).InitializeAsync();
            await new PostgresSessionExecutionStore(config).InitializeAsync();
            await new PostgresWebhookTriggerStore(config).InitializeAsync();
            await new PostgresSessionMessageStore(config).InitializeAsync();
            await new SingleUserOwnerStore(config).InitializeAsync();
            await new OwnerHandoverStore(config).InitializeAsync();
            return new HandoverDatabase(baseCs, schema, config);
        }

        /// <summary>admin (the API-key owner) with three sessions, one scheduled; alice (the target) with
        /// clashing names; bob and carol as bystanders.</summary>
        public async Task SeedAsync()
        {
            var users = new UserDirectory(Config);
            foreach (var u in new[] { "admin", "alice", "bob", "carol" })
                await users.RecordLoginAsync(u, $"{u}@example.com", u);
            var oldKey = KubernetesSessionService.OwnerKey(From);
            await ExecAsync($$"""
                INSERT INTO sessions (id, owner, mode, claude_session_id, callback_token, agent_session_id, project_id) VALUES
                    ('s1', 'admin', 'Interactive', 'c1', 't1', 'c1', 'p1'),
                    ('s2', 'admin', 'Autonomous', 'c2', 't2', 'c2', NULL),
                    ('s3', 'admin', 'Scheduled', 'c3', 't3', 'c3', NULL),
                    ('sa', 'alice', 'Interactive', 'ca', 'ta', 'ca', 'pa'),
                    ('sb', 'bob', 'Interactive', 'cb', 'tb', 'cb', 'pb');
                INSERT INTO projects (id, owner, name) VALUES
                    ('p1', 'admin', 'Tools'), ('p2', 'admin', 'Other'), ('pa', 'alice', 'tools'), ('pb', 'bob', 'Bobs');
                INSERT INTO project_shares (project_id, subject_type, subject, role) VALUES
                    ('p1', 'user', 'alice', 'Viewer'), ('pb', 'user', 'admin', 'Viewer');
                INSERT INTO session_shares (session_id, recipient_owner, role) VALUES
                    ('s1', 'alice', 'Viewer'), ('s1', 'carol', 'Viewer'),
                    ('sb', 'admin', 'Viewer'), ('sb', 'alice', 'Collaborator');
                INSERT INTO skills (id, owner, name, description, content) VALUES
                    ('k1', 'admin', 'deploy', '', 'x'), ('k2', 'alice', 'deploy', '', 'y'), ('k3', 'admin', 'review', '', 'z');
                INSERT INTO mcp_servers (id, owner, name, config_json) VALUES
                    ('m1', 'admin', 'github', '{}'), ('m2', 'alice', 'GitHub', '{}');
                INSERT INTO session_usage (session_id, owner, cost_usd) VALUES ('s1', 'admin', 2.0);
                INSERT INTO usage_monthly (owner, month, cost_usd) VALUES
                    ('admin', date '2026-10-01', 2.0), ('alice', date '2026-10-01', 3.0);
                INSERT INTO session_files (id, session_id, owner, name, extension, declared_mime_type, size, storage_kind,
                        storage_locator, state, preview_state, creator, source, created_at) VALUES
                    ('f1', 's1', 'admin', 'a.txt', '.txt', 'text/plain', 5, 's3', 'sessions/admin/s1/files/f1/a.txt',
                     'Ready', 'None', 'admin', 'upload', now());
                INSERT INTO session_executions (session_id, generation, agent, native_id, transcript_key, state_key) VALUES
                    ('s1', 0, 'Claude', 'c1', 'sessions/{{oldKey}}/s1/transcript.jsonl', 'sessions/{{oldKey}}/s1/claude-state.tgz');
                INSERT INTO api_tokens (id, owner, name, token_hash, token_prefix) VALUES ('tok1', 'admin', 'cli', 'h', 'oah_');
                """);
            await new ChatBindingStore(Config).UpsertAsync(new ChatBinding("telegram", "s1", "admin", "chat-1", null, null, true));
        }

        /// <summary>Every row of the tables a handover touches, as text, for before/after comparisons.</summary>
        public async Task<string> SnapshotAsync()
        {
            var parts = new List<string>();
            foreach (var table in new[] { "sessions", "projects", "project_shares", "session_shares", "skills", "mcp_servers",
                         "session_usage", "usage_monthly", "session_files", "session_executions", "api_tokens",
                         "chat_session_bindings", "library_shares", "owner_handovers" })
                parts.Add(table + ":" + await ScalarAsync<string>(
                    $"SELECT coalesce(string_agg(t::text, '|' ORDER BY t::text), '') FROM {table} t"));
            return string.Join("\n", parts);
        }

        public async Task ExecAsync(string sql)
        {
            await using var cmd = _raw.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql) => await ScalarAsync<long>(sql);

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var cmd = _raw.CreateCommand(sql);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await _raw.DisposeAsync();
            await using var c = new NpgsqlConnection(_base);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", c);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
