using System.Security.Cryptography;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Npgsql;
using NpgsqlTypes;

namespace AgentHub.Api.Persistence;

public sealed record ProviderSwitchTarget(AgentKind Agent, AgentAuthMode AuthMode, string? CredentialId,
    string? ModelEndpointUrl, string? ModelName);
public sealed record ProviderSwitchRequest(Guid RequestId, string SessionId, long SourceGeneration,
    AgentKind SourceAgent, string SourcePodUid, string ClaimUid, ProviderSwitchTarget Target, string Handover, string? ReviewHash = null);
public sealed record ProviderSwitchProgress(string Phase, string? TargetPodUid = null,
    string? SourceNativeId = null, IReadOnlyList<TranscriptEntry>? SourceEntries = null, string? Error = null, string? TargetNativeId = null, bool SourceStopAcknowledged = false);
public sealed record ProviderSwitchOperation(ProviderSwitchRequest Request, ProviderSwitchProgress Progress);

public interface IProviderSwitchStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task CreateAsync(string owner, ProviderSwitchRequest request, CancellationToken ct = default);
    Task<ProviderSwitchOperation?> GetAsync(string owner, string id, CancellationToken ct = default);
    Task<bool> CanConsumeInboxAsync(string owner, string id, long generation, CancellationToken ct = default);
    Task<IReadOnlyList<(string Owner, string SessionId)>> PendingAsync(CancellationToken ct = default);
    Task MoveAsync(string owner, string id, Guid requestId, string expectedPhase, ProviderSwitchProgress progress, CancellationToken ct = default);
    Task AdmitTargetAsync(string owner, string id, Guid requestId, CancellationToken ct = default);
}

public sealed class PostgresProviderSwitchStore(IConfiguration configuration, IExecutionFence fence, IExecutionUsageSnapshots? usageSnapshots = null,
    AgentHub.Api.Permissions.PermissionStore? permissions = null) : IProviderSwitchStore
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(configuration);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS session_provider_switches (
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                request_id UUID NOT NULL, request JSONB NOT NULL, progress JSONB NOT NULL,
                phase TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY(session_id,request_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS session_provider_switch_active
                ON session_provider_switches(session_id) WHERE phase NOT IN ('completed','cancelled');
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateAsync(string owner, ProviderSwitchRequest request, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.SourceGeneration < 0 || request.Handover.Length is 0 or > 16000
            || string.IsNullOrEmpty(request.SourcePodUid) || string.IsNullOrEmpty(request.ClaimUid)
            || request.SourceAgent is not (AgentKind.Claude or AgentKind.Codex)
            || request.Target.Agent is not (AgentKind.Claude or AgentKind.Codex) || request.SourceAgent == request.Target.Agent)
            throw new ArgumentException("Invalid provider replacement request.");
        AgentConfiguration.ValidateForCreate(request.Target.Agent, request.Target.AuthMode);
        await using var admission = await fence.WriteAsync(request.SessionId, ct);
        // Lock the registry before admission so two owners' requests cannot both observe it idle.
        await using var command = _db.CreateCommand("""
            WITH owned AS (
                SELECT id FROM sessions WHERE id=@id AND owner=@owner AND workspace_persistent
                    AND NOT provider_switch_pending AND execution_generation=@generation FOR UPDATE
            ), admitted AS (
                INSERT INTO session_provider_switches(session_id,request_id,request,progress,phase)
                SELECT id,@requestId,@request,@progress,'requested' FROM owned
                ON CONFLICT DO NOTHING RETURNING session_id
            ) UPDATE sessions SET provider_switch_pending=TRUE WHERE id IN (SELECT session_id FROM admitted)
            """);
        Identity(command, owner, request.SessionId, request.RequestId);
        command.Parameters.AddWithValue("generation", request.SourceGeneration);
        command.Parameters.AddWithValue("request", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(request, Json));
        command.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new ProviderSwitchProgress("requested"), Json));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Session changed or replacement is already pending.");
    }

    public async Task<ProviderSwitchOperation?> GetAsync(string owner, string id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT p.request::text,p.progress::text FROM session_provider_switches p JOIN sessions s ON s.id=p.session_id
            WHERE s.owner=@owner AND s.id=@id ORDER BY p.updated_at DESC,p.request_id DESC LIMIT 1
            """);
        command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(JsonSerializer.Deserialize<ProviderSwitchRequest>(reader.GetString(0), Json)!,
            JsonSerializer.Deserialize<ProviderSwitchProgress>(reader.GetString(1), Json)!) : null;
    }

    public async Task<IReadOnlyList<(string Owner, string SessionId)>> PendingAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT s.owner,s.id FROM sessions s JOIN session_provider_switches p ON p.session_id=s.id
            WHERE p.phase NOT IN ('completed','cancelled') AND p.progress->>'error' IS NULL
            ORDER BY p.updated_at LIMIT 100
            """);
        var result = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task<bool> CanConsumeInboxAsync(string owner, string id, long generation, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM session_provider_switches p JOIN sessions s ON s.id=p.session_id
                WHERE s.id=@id AND s.owner=@owner AND s.execution_generation=@generation
                AND ((p.phase='releasing' AND (p.request->>'sourceGeneration')::bigint+1=@generation)
                    OR (p.phase='cancelling' AND (p.request->>'sourceGeneration')::bigint=@generation)))
            """);
        command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("generation", generation);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task MoveAsync(string owner, string id, Guid requestId, string expectedPhase, ProviderSwitchProgress progress, CancellationToken ct = default)
    {
        var allowed = expectedPhase == progress.Phase || (expectedPhase, progress.Phase) is
            ("requested", "prepared") or ("requested", "cancelling") or ("prepared", "cancelling") or ("cancelling", "cancelled")
            or ("prepared", "stopping") or ("stopping", "source-stopped") or ("target-admitted", "target-starting") or ("target-starting", "target-started")
            or ("target-started", "committing") or ("committing", "releasing") or ("releasing", "completed");
        if (!allowed || (progress.Phase == "source-stopped" && !progress.SourceStopAcknowledged) || (progress.Phase == "committing" && !Guid.TryParse(progress.TargetNativeId, out _)))
            throw new ArgumentException("Invalid replacement transition or target native identity.");
        await using var command = _db.CreateCommand("""
            WITH moved AS (
                UPDATE session_provider_switches p SET progress=@progress,phase=@next,updated_at=now() FROM sessions s
                WHERE p.session_id=s.id AND s.owner=@owner AND s.id=@id AND p.request_id=@requestId AND p.phase=@expected
                RETURNING p.session_id
            ) UPDATE sessions SET provider_switch_pending=(@next NOT IN ('completed','cancelled')),
                agent_session_id=CASE WHEN @next='committing' THEN @native ELSE agent_session_id END
                WHERE id IN (SELECT session_id FROM moved)
            """);
        Identity(command, owner, id, requestId);
        command.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(progress, Json));
        command.Parameters.AddWithValue("native", (object?)progress.TargetNativeId ?? DBNull.Value);
        command.Parameters.AddWithValue("expected", expectedPhase); command.Parameters.AddWithValue("next", progress.Phase);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Replacement state changed.");
    }

    public async Task AdmitTargetAsync(string owner, string id, Guid requestId, CancellationToken ct = default)
    {
        // Final source callbacks and credential pushes must finish before authority is rotated.
        // The worker holds the lifecycle lock and has already observed source pod disappearance.
        await using var authority = await fence.WriteAsync(id, ct);
        if (usageSnapshots is not null)
        {
            var checkpoint = await GetAsync(owner, id, ct);
            if (checkpoint is null || checkpoint.Request.RequestId != requestId || checkpoint.Progress.Phase != "source-stopped")
                throw new InvalidOperationException("Replacement is not ready for target admission.");
            // Finish this FK-backed snapshot before locking the registry row below: a second
            // connection inserting its session reference would otherwise wait on our own lock.
            await usageSnapshots.CaptureAsync(owner, id, checkpoint.Request.SourceGeneration, ct);
        }
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        ProviderSwitchRequest request;
        ProviderSwitchProgress progress;
        await using (var read = new NpgsqlCommand("""
            SELECT p.request::text,p.progress::text FROM session_provider_switches p JOIN sessions s ON s.id=p.session_id
            WHERE s.id=@id AND s.owner=@owner AND p.request_id=@requestId AND p.phase='source-stopped'
                AND s.provider_switch_pending AND s.workspace_persistent FOR UPDATE OF s,p
            """, connection, transaction))
        {
            Identity(read, owner, id, requestId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Replacement is not ready for target admission.");
            request = JsonSerializer.Deserialize<ProviderSwitchRequest>(reader.GetString(0), Json)!;
            progress = JsonSerializer.Deserialize<ProviderSwitchProgress>(reader.GetString(1), Json)!;
        }
        if (permissions is not null && (await permissions.GetPendingRequestsAsync(id, ct)).Count > 0)
            throw new InvalidOperationException("Source approvals are still pending; resolve them before target admission.");
        await using (var update = new NpgsqlCommand("""
            UPDATE sessions SET agent=@agent,auth_mode=@auth,credential_id=@credential,resolved_credential_id=@credential,
                model_endpoint_url=@endpoint,model_name=@model,agent_session_id=@native,callback_token=@token,
                execution_generation=execution_generation+1,status='Pending',question_pending=FALSE,question_open=FALSE,question_title=NULL
            WHERE id=@id AND owner=@owner AND execution_generation=@generation AND provider_switch_pending
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("owner", owner);
            update.Parameters.AddWithValue("generation", request.SourceGeneration);
            update.Parameters.AddWithValue("agent", request.Target.Agent.ToString()); update.Parameters.AddWithValue("auth", request.Target.AuthMode.ToString());
            update.Parameters.AddWithValue("credential", (object?)request.Target.CredentialId ?? DBNull.Value);
            update.Parameters.AddWithValue("endpoint", (object?)request.Target.ModelEndpointUrl ?? DBNull.Value);
            update.Parameters.AddWithValue("model", (object?)request.Target.ModelName ?? DBNull.Value);
            update.Parameters.AddWithValue("native", Guid.NewGuid().ToString());
            update.Parameters.AddWithValue("token", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Execution generation changed.");
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE session_provider_switches SET phase='target-admitted',progress=@progress,updated_at=now()
            WHERE session_id=@id AND request_id=@requestId
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("requestId", requestId);
            update.Parameters.AddWithValue("progress", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(progress with { Phase = "target-admitted", Error = null }, Json));
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static void Identity(NpgsqlCommand command, string owner, string id, Guid requestId)
    {
        command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("requestId", requestId);
    }
}
