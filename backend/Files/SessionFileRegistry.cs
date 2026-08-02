using Npgsql;

namespace AgentHub.Api.Files;

public interface ISessionFileRegistry
{
    Task InitializeAsync(CancellationToken ct = default);
    Task InsertAsync(SessionFileRecord file, CancellationToken ct = default);
    async Task<SessionFileReservationResult> TryReserveAsync(
        SessionFileRecord file, int maxFiles, long maxBytes, CancellationToken ct = default)
    {
        var active = await ListAsync(file.SessionId, ct);
        if (active.Count >= maxFiles) return SessionFileReservationResult.CountExceeded;
        var bytes = active.Aggregate<SessionFileRecord, long>(0,
            (total, item) => checked(total + item.Size));
        if (file.Size > maxBytes - bytes) return SessionFileReservationResult.BytesExceeded;
        await InsertAsync(file, ct);
        return SessionFileReservationResult.Inserted;
    }
    Task<SessionFileRecord?> GetAsync(
        string sessionId, string fileId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionFileRecord>> ListAsync(
        string sessionId, CancellationToken ct = default);
    Task<SessionFileUsage> GetUsageAsync(string sessionId, CancellationToken ct = default);
    Task<bool> TransitionAsync(
        string sessionId,
        string fileId,
        SessionFileState expected,
        SessionFileState next,
        string? detectedMime,
        long? actualSize,
        CancellationToken ct = default);
    Task<SessionFilePresentation?> GetPresentationAsync(
        string sessionId, CancellationToken ct = default);
    Task<SessionFilePresentation> SetPresentationAsync(
        string sessionId,
        string? fileId,
        string presenter,
        CancellationToken ct = default);
    async Task ClearPresentationIfFileAsync(
        string sessionId, string fileId, string presenter, CancellationToken ct = default)
    {
        var current = await GetPresentationAsync(sessionId, ct);
        if (current?.FileId == fileId)
        {
            await SetPresentationAsync(sessionId, null, presenter, ct);
        }
    }
    Task<SessionFileRecord?> ClaimPreviewAsync(CancellationToken ct = default);
    Task LinkPreviewAsync(
        string sourceId,
        string previewId,
        bool succeeded,
        CancellationToken ct = default);
    Task<IReadOnlyList<SessionFileRecord>> ExpireReservationsAsync(
        DateTime cutoff, CancellationToken ct = default);
    Task<IReadOnlyList<SessionFileRecord>> ListReadyPodFilesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SessionFileRecord>>(Array.Empty<SessionFileRecord>());
    Task MarkSessionDeletedAsync(string sessionId, CancellationToken ct = default);
}

public sealed class PostgresSessionFileRegistry : ISessionFileRegistry
{
    private const string FileColumns = """
        id, session_id, owner, name, extension, declared_mime_type,
        detected_mime_type, size, storage_kind, storage_locator, state,
        preview_state, preview_file_id, creator, source, created_at,
        completed_at, expires_at
        """;

    private readonly NpgsqlDataSource _db;

    public PostgresSessionFileRegistry(IConfiguration cfg)
    {
        var connectionString = cfg.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(connectionString);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS session_files (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                owner TEXT NOT NULL,
                name TEXT NOT NULL,
                extension TEXT NOT NULL,
                declared_mime_type TEXT NOT NULL,
                detected_mime_type TEXT,
                size BIGINT NOT NULL CHECK (size >= 0),
                storage_kind TEXT NOT NULL,
                storage_locator TEXT NOT NULL,
                state TEXT NOT NULL,
                preview_state TEXT NOT NULL,
                preview_file_id TEXT REFERENCES session_files(id) ON DELETE SET NULL,
                creator TEXT NOT NULL,
                source TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                completed_at TIMESTAMPTZ,
                expires_at TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_session_files_session_created
                ON session_files(session_id, created_at, id);
            CREATE INDEX IF NOT EXISTS idx_session_files_active_usage
                ON session_files(session_id, state);
            CREATE INDEX IF NOT EXISTS idx_session_files_preview_queue
                ON session_files(created_at, id)
                WHERE state = 'Ready' AND preview_state = 'Queued';
            CREATE INDEX IF NOT EXISTS idx_session_files_reservation_expiry
                ON session_files(created_at)
                WHERE state IN ('Reserved', 'Uploading');

            CREATE TABLE IF NOT EXISTS session_file_presentations (
                session_id TEXT PRIMARY KEY,
                file_id TEXT REFERENCES session_files(id) ON DELETE SET NULL,
                revision BIGINT NOT NULL,
                presenter TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );
            """;

        await using var command = _db.CreateCommand(ddl);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionFileReservationResult> TryReserveAsync(
        SessionFileRecord file, int maxFiles, long maxBytes, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var sessionLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@session, 0))", connection, transaction))
        {
            sessionLock.Parameters.AddWithValue("session", file.SessionId);
            await sessionLock.ExecuteNonQueryAsync(ct);
        }

        int count;
        long bytes;
        await using (var usage = new NpgsqlCommand("""
            SELECT count(*)::int, COALESCE(sum(size), 0)::bigint
            FROM session_files
            WHERE session_id = @session AND state NOT IN ('Expired', 'Deleted')
            """, connection, transaction))
        {
            usage.Parameters.AddWithValue("session", file.SessionId);
            await using var reader = await usage.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            count = reader.GetInt32(0);
            bytes = reader.GetInt64(1);
        }

        var result = count >= maxFiles
            ? SessionFileReservationResult.CountExceeded
            : file.Size > maxBytes - bytes
                ? SessionFileReservationResult.BytesExceeded
                : SessionFileReservationResult.Inserted;
        if (result == SessionFileReservationResult.Inserted)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO session_files (
                    id, session_id, owner, name, extension, declared_mime_type,
                    detected_mime_type, size, storage_kind, storage_locator, state,
                    preview_state, preview_file_id, creator, source, created_at,
                    completed_at, expires_at)
                VALUES (@id, @session, @owner, @name, @extension, @declaredMime,
                    @detectedMime, @size, @storageKind, @storageLocator, @state,
                    @previewState, @previewFileId, @creator, @source, @createdAt,
                    @completedAt, @expiresAt)
                """, connection, transaction);
            AddFileParameters(insert, file);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task InsertAsync(SessionFileRecord file, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO session_files (
                id, session_id, owner, name, extension, declared_mime_type,
                detected_mime_type, size, storage_kind, storage_locator, state,
                preview_state, preview_file_id, creator, source, created_at,
                completed_at, expires_at)
            VALUES (
                @id, @session, @owner, @name, @extension, @declaredMime,
                @detectedMime, @size, @storageKind, @storageLocator, @state,
                @previewState, @previewFileId, @creator, @source, @createdAt,
                @completedAt, @expiresAt)
            """);
        AddFileParameters(command, file);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionFileRecord?> GetAsync(
        string sessionId, string fileId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand($"""
            SELECT {FileColumns}
            FROM session_files
            WHERE session_id = @session AND id = @id
            """);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("id", fileId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapFile(reader) : null;
    }

    public async Task<IReadOnlyList<SessionFileRecord>> ListAsync(
        string sessionId, CancellationToken ct = default)
    {
        var files = new List<SessionFileRecord>();
        await using var command = _db.CreateCommand($"""
            SELECT {FileColumns}
            FROM session_files
            WHERE session_id = @session AND state NOT IN ('Expired', 'Deleted')
            ORDER BY created_at, id
            """);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            files.Add(MapFile(reader));
        }

        return files;
    }

    public async Task<SessionFileUsage> GetUsageAsync(
        string sessionId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT count(*)::int, COALESCE(sum(size), 0)::bigint
            FROM session_files
            WHERE session_id = @session AND state = 'Ready'
            """);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new SessionFileUsage(reader.GetInt32(0), reader.GetInt64(1));
    }

    public async Task<bool> TransitionAsync(
        string sessionId,
        string fileId,
        SessionFileState expected,
        SessionFileState next,
        string? detectedMime,
        long? actualSize,
        CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            UPDATE session_files
            SET state = @next,
                detected_mime_type = COALESCE(@detectedMime, detected_mime_type),
                size = COALESCE(@actualSize, size),
                completed_at = CASE WHEN @next = 'Ready' THEN now() ELSE completed_at END
            WHERE session_id = @session AND id = @id AND state = @expected
            """);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("id", fileId);
        command.Parameters.AddWithValue("expected", expected.ToString());
        command.Parameters.AddWithValue("next", next.ToString());
        command.Parameters.AddWithValue("detectedMime", DbValue(detectedMime));
        command.Parameters.AddWithValue("actualSize", DbValue(actualSize));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<SessionFilePresentation?> GetPresentationAsync(
        string sessionId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT session_id, file_id, revision, presenter, updated_at
            FROM session_file_presentations
            WHERE session_id = @session
            """);
        command.Parameters.AddWithValue("session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapPresentation(reader) : null;
    }

    public async Task<SessionFilePresentation> SetPresentationAsync(
        string sessionId,
        string? fileId,
        string presenter,
        CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO session_file_presentations (
                session_id, file_id, revision, presenter, updated_at)
            SELECT @session, @fileId, 1, @presenter, now()
            WHERE @fileId IS NULL OR EXISTS (
                SELECT 1 FROM session_files
                WHERE session_id = @session AND id = @fileId AND state = 'Ready')
            ON CONFLICT (session_id) DO UPDATE
            SET file_id = EXCLUDED.file_id,
                revision = session_file_presentations.revision + 1,
                presenter = EXCLUDED.presenter,
                updated_at = now()
            RETURNING session_id, file_id, revision, presenter, updated_at
            """);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("fileId", DbValue(fileId));
        command.Parameters.AddWithValue("presenter", presenter);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new KeyNotFoundException("The presentation file is not ready in this session.");
        }

        return MapPresentation(reader);
    }

    public async Task ClearPresentationIfFileAsync(
        string sessionId, string fileId, string presenter, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            UPDATE session_file_presentations
            SET file_id = NULL,
                revision = revision + 1,
                presenter = @presenter,
                updated_at = now()
            WHERE session_id = @session AND file_id = @fileId
            """);
        command.Parameters.AddWithValue("session", sessionId);
        command.Parameters.AddWithValue("fileId", fileId);
        command.Parameters.AddWithValue("presenter", presenter);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionFileRecord?> ClaimPreviewAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand($"""
            WITH claimed AS (
                SELECT id
                FROM session_files
                WHERE state = 'Ready' AND preview_state = 'Queued'
                ORDER BY created_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE session_files AS files
            SET preview_state = 'Converting'
            FROM claimed
            WHERE files.id = claimed.id
            RETURNING {QualifyFileColumns("files")}
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapFile(reader) : null;
    }

    public async Task LinkPreviewAsync(
        string sourceId,
        string previewId,
        bool succeeded,
        CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            UPDATE session_files
            SET preview_state = @previewState,
                preview_file_id = CASE WHEN @succeeded THEN @previewId ELSE NULL END
            WHERE id = @sourceId AND preview_state = 'Converting'
            """);
        command.Parameters.AddWithValue("previewState",
            succeeded ? SessionFilePreviewState.Ready.ToString() : SessionFilePreviewState.Failed.ToString());
        command.Parameters.AddWithValue("succeeded", succeeded);
        command.Parameters.AddWithValue("previewId", previewId);
        command.Parameters.AddWithValue("sourceId", sourceId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException("The preview source is not being converted.");
        }
    }

    public async Task<IReadOnlyList<SessionFileRecord>> ExpireReservationsAsync(
        DateTime cutoff, CancellationToken ct = default)
    {
        var files = new List<SessionFileRecord>();
        await using var command = _db.CreateCommand($"""
            UPDATE session_files
            SET state = 'Expired'
            WHERE state IN ('Reserved', 'Uploading') AND created_at < @cutoff
            RETURNING {FileColumns}
            """);
        command.Parameters.AddWithValue("cutoff", EnsureUtc(cutoff));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            files.Add(MapFile(reader));
        }

        return files;
    }

    public async Task<IReadOnlyList<SessionFileRecord>> ListReadyPodFilesAsync(
        CancellationToken ct = default)
    {
        var files = new List<SessionFileRecord>();
        await using var command = _db.CreateCommand($"""
            SELECT {FileColumns}
            FROM session_files
            WHERE storage_kind = 'Pod' AND state = 'Ready'
            ORDER BY session_id, created_at, id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) files.Add(MapFile(reader));
        return files;
    }

    public async Task MarkSessionDeletedAsync(
        string sessionId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var files = new NpgsqlCommand("""
            UPDATE session_files
            SET state = 'Deleted'
            WHERE session_id = @session AND state <> 'Deleted'
            """, connection, transaction))
        {
            files.Parameters.AddWithValue("session", sessionId);
            await files.ExecuteNonQueryAsync(ct);
        }

        await using (var presentation = new NpgsqlCommand("""
            UPDATE session_file_presentations
            SET file_id = NULL, revision = revision + 1, updated_at = now()
            WHERE session_id = @session
            """, connection, transaction))
        {
            presentation.Parameters.AddWithValue("session", sessionId);
            await presentation.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static void AddFileParameters(NpgsqlCommand command, SessionFileRecord file)
    {
        command.Parameters.AddWithValue("id", file.Id);
        command.Parameters.AddWithValue("session", file.SessionId);
        command.Parameters.AddWithValue("owner", file.Owner);
        command.Parameters.AddWithValue("name", file.Name);
        command.Parameters.AddWithValue("extension", file.Extension);
        command.Parameters.AddWithValue("declaredMime", file.DeclaredMimeType);
        command.Parameters.AddWithValue("detectedMime", DbValue(file.DetectedMimeType));
        command.Parameters.AddWithValue("size", file.Size);
        command.Parameters.AddWithValue("storageKind", file.StorageKind.ToString());
        command.Parameters.AddWithValue("storageLocator", file.StorageLocator);
        command.Parameters.AddWithValue("state", file.State.ToString());
        command.Parameters.AddWithValue("previewState", file.PreviewState.ToString());
        command.Parameters.AddWithValue("previewFileId", DbValue(file.PreviewFileId));
        command.Parameters.AddWithValue("creator", file.Creator);
        command.Parameters.AddWithValue("source", file.Source);
        command.Parameters.AddWithValue("createdAt", EnsureUtc(file.CreatedAt));
        command.Parameters.AddWithValue("completedAt", DbValue(file.CompletedAt));
        command.Parameters.AddWithValue("expiresAt", DbValue(file.ExpiresAt));
    }

    private static SessionFileRecord MapFile(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt64(7),
        Enum.Parse<SessionFileStorageKind>(reader.GetString(8), ignoreCase: false),
        reader.GetString(9),
        Enum.Parse<SessionFileState>(reader.GetString(10), ignoreCase: false),
        Enum.Parse<SessionFilePreviewState>(reader.GetString(11), ignoreCase: false),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetString(13),
        reader.GetString(14),
        EnsureUtc(reader.GetDateTime(15)),
        reader.IsDBNull(16) ? null : EnsureUtc(reader.GetDateTime(16)),
        reader.IsDBNull(17) ? null : EnsureUtc(reader.GetDateTime(17)));

    private static SessionFilePresentation MapPresentation(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetInt64(2),
        reader.GetString(3),
        EnsureUtc(reader.GetDateTime(4)));

    private static string QualifyFileColumns(string alias) => string.Join(", ",
        FileColumns.Split(',', StringSplitOptions.TrimEntries).Select(column => $"{alias}.{column}"));

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
    private static object DbValue(long? value) => value.HasValue ? value.Value : DBNull.Value;
    private static object DbValue(DateTime? value) =>
        value.HasValue ? EnsureUtc(value.Value) : DBNull.Value;

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
