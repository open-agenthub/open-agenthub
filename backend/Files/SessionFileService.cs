using AgentHub.Api.Models;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;

namespace AgentHub.Api.Files;

public sealed record SessionFileActor(
    string SessionId,
    string Owner,
    string Principal,
    bool CanWrite,
    bool CanManage);

public sealed record ReserveSessionFileCommand(
    string Name,
    string MimeType,
    long Size,
    string? BatchId,
    string Source);

public sealed record FileUploadDescriptor(
    string Kind,
    string Url,
    IReadOnlyDictionary<string, string> Headers);

public sealed record ReserveFileResult(
    SessionFileRecord File,
    FileUploadDescriptor Upload);

public sealed record FileContentResult(
    Stream? Content,
    string? RedirectUrl,
    string MimeType,
    string Name,
    long Size);

public sealed class SessionFileException(string code, string? message = null)
    : Exception(message ?? code)
{
    public string Code { get; } = code;
}

public interface ISessionFileService
{
    Task<ReserveFileResult> ReserveAsync(
        SessionFileActor actor,
        ReserveSessionFileCommand request,
        CancellationToken ct = default);
    Task PutPodContentAsync(
        SessionFileActor actor,
        string fileId,
        Stream content,
        long? contentLength = null,
        CancellationToken ct = default);
    Task<SessionFileRecord> CompleteAsync(
        SessionFileActor actor,
        string fileId,
        CancellationToken ct = default);
    Task<IReadOnlyList<SessionFileRecord>> ListAsync(
        SessionFileActor actor,
        CancellationToken ct = default);
    Task<FileContentResult> OpenContentAsync(
        SessionFileActor actor,
        string fileId,
        CancellationToken ct = default);
    Task DeleteAsync(
        SessionFileActor actor,
        string fileId,
        CancellationToken ct = default);
    Task<SessionFilePresentation?> GetPresentationAsync(
        SessionFileActor actor,
        CancellationToken ct = default);
    Task<SessionFilePresentation> SetPresentationAsync(
        SessionFileActor actor,
        string? fileId,
        CancellationToken ct = default);
    Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default);
}

public sealed class SessionFileService : ISessionFileService
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>();

    private readonly ISessionFileRegistry _registry;
    private readonly IArtifactStore _artifacts;
    private readonly IAgentFileClient _agentFiles;
    private readonly ISessionService _sessions;
    private readonly SessionFileOptions _options;
    private readonly ILogger<SessionFileService> _log;

    public SessionFileService(
        ISessionFileRegistry registry,
        IArtifactStore artifacts,
        IAgentFileClient agentFiles,
        ISessionService sessions,
        SessionFileOptions options,
        ILogger<SessionFileService>? logger = null)
    {
        _registry = registry;
        _artifacts = artifacts;
        _agentFiles = agentFiles;
        _sessions = sessions;
        _options = options;
        _log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionFileService>.Instance;
    }

    public async Task<ReserveFileResult> ReserveAsync(
        SessionFileActor actor,
        ReserveSessionFileCommand request,
        CancellationToken ct = default)
    {
        RequireWrite(actor);
        var validation = SessionFileValidator.ValidateReservation(
            _options, request.Name, request.MimeType, request.Size);
        if (!validation.Allowed)
        {
            throw new SessionFileException(validation.Code!);
        }

        SessionInfo? liveSession = null;
        var storageKind = _artifacts.IsConfigured
            ? SessionFileStorageKind.S3
            : SessionFileStorageKind.Pod;
        if (storageKind == SessionFileStorageKind.Pod)
        {
            liveSession = await GetLiveSessionAsync(actor, ct);
        }

        var id = Guid.NewGuid().ToString("n");
        var extension = Path.GetExtension(request.Name).ToLowerInvariant();
        var locator = storageKind == SessionFileStorageKind.S3
            ? IArtifactStore.SessionFileKey(actor.Owner, actor.SessionId, id, request.Name)
            : $"{id}/{request.Name}";
        var file = new SessionFileRecord(
            id,
            actor.SessionId,
            actor.Owner,
            request.Name,
            extension,
            request.MimeType,
            null,
            request.Size,
            storageKind,
            locator,
            SessionFileState.Reserved,
            IsOffice(extension) ? SessionFilePreviewState.Queued : SessionFilePreviewState.None,
            null,
            actor.Principal,
            string.IsNullOrWhiteSpace(request.Source) ? "user" : request.Source.Trim(),
            DateTime.UtcNow,
            null,
            storageKind == SessionFileStorageKind.Pod ? null : DateTime.UtcNow.AddMinutes(_options.ReservationMinutes));

        // A presigned URL only helps when the browser can reach object storage. With the
        // usual cluster-internal endpoint it cannot, so the bytes go through the API.
        var upload = storageKind == SessionFileStorageKind.S3 && _artifacts.CanServeBrowsersDirectly
            ? new FileUploadDescriptor(
                "presigned",
                _artifacts.PresignPut(locator, TimeSpan.FromMinutes(_options.PresignMinutes)),
                new Dictionary<string, string> { ["Content-Type"] = request.MimeType })
            : new FileUploadDescriptor(
                "proxy",
                $"/api/sessions/{Uri.EscapeDataString(actor.SessionId)}/files/{id}/content",
                NoHeaders);

        var reservation = await _registry.TryReserveAsync(
            file, _options.MaxSessionFiles, _options.MaxSessionBytes, ct);
        if (reservation == SessionFileReservationResult.CountExceeded)
        {
            throw new SessionFileException("session_file_count_exceeded");
        }
        if (reservation == SessionFileReservationResult.BytesExceeded)
        {
            throw new SessionFileException("session_file_bytes_exceeded");
        }
        _ = liveSession;
        return new ReserveFileResult(file, upload);
    }

    public async Task PutPodContentAsync(
        SessionFileActor actor,
        string fileId,
        Stream content,
        long? contentLength = null,
        CancellationToken ct = default)
    {
        // Resolve before wrapping: SizeLimitedReadStream reports neither Length nor CanSeek.
        var length = contentLength ?? (content.CanSeek ? content.Length - content.Position : null);
        RequireWrite(actor);
        var file = await RequireFileAsync(actor.SessionId, fileId, ct);
        if (file.State != SessionFileState.Reserved)
        {
            throw new SessionFileException("file_state_conflict");
        }

        // S3-backed files take the same route whenever storage is not reachable from the
        // browser; the API streams them on rather than handing out a presigned URL.
        if (file.StorageKind == SessionFileStorageKind.S3)
        {
            await using var limitedS3 = new SizeLimitedReadStream(content, file.Size);
            try
            {
                await _artifacts.TryPutStreamAsync(
                    file.StorageLocator, limitedS3, file.DeclaredMimeType, length, ct);
            }
            catch (InvalidDataException)
            {
                throw new SessionFileException("file_too_large");
            }

            if (!await _registry.TransitionAsync(actor.SessionId, file.Id,
                    SessionFileState.Reserved, SessionFileState.Uploading, null, null, ct))
            {
                throw new SessionFileException("file_state_conflict");
            }
            return;
        }

        var liveSession = await GetLiveSessionAsync(actor, ct);
        await using var limited = new SizeLimitedReadStream(content, file.Size);
        try
        {
            await _agentFiles.PutAsync(liveSession, file, limited, ct);
        }
        catch (SessionFileException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw new SessionFileException("file_too_large");
        }

        if (!await _registry.TransitionAsync(actor.SessionId, file.Id,
                SessionFileState.Reserved, SessionFileState.Uploading, null, null, ct))
        {
            throw new SessionFileException("file_state_conflict");
        }
    }

    public async Task<SessionFileRecord> CompleteAsync(
        SessionFileActor actor,
        string fileId,
        CancellationToken ct = default)
    {
        RequireWrite(actor);
        var file = await RequireFileAsync(actor.SessionId, fileId, ct);
        var expected = file.StorageKind == SessionFileStorageKind.Pod
            ? SessionFileState.Uploading
            : SessionFileState.Reserved;
        if (file.State != expected)
        {
            throw new SessionFileException("file_state_conflict");
        }

        Stream? source;
        if (file.StorageKind == SessionFileStorageKind.S3)
        {
            var metadata = await _artifacts.HeadAsync(file.StorageLocator, ct);
            if (metadata is null || metadata.Size != file.Size ||
                (!string.IsNullOrWhiteSpace(metadata.ContentType) &&
                 !string.Equals(metadata.ContentType, file.DeclaredMimeType, StringComparison.OrdinalIgnoreCase)))
            {
                await FailAsync(file, expected, ct);
                throw new SessionFileException("storage_verification_failed");
            }

            source = await _artifacts.OpenReadAsync(file.StorageLocator, ct);
        }
        else
        {
            var liveSession = await GetLiveSessionAsync(actor, ct);
            if (!await _agentFiles.ExistsAsync(liveSession, file, ct))
            {
                await FailAsync(file, expected, ct);
                throw new SessionFileException("storage_verification_failed");
            }

            source = await _agentFiles.OpenReadAsync(liveSession, file, ct);
        }

        if (source is null)
        {
            await FailAsync(file, expected, ct);
            throw new SessionFileException("storage_verification_failed");
        }

        await using (source)
        {
            var buffered = await BufferAsync(source, _options.MaxDocumentBytes, ct);
            if (buffered is null || buffered.Length != file.Size)
            {
                await FailAsync(file, expected, ct);
                throw new SessionFileException("storage_verification_failed");
            }

            await using var detectionStream = new MemoryStream(buffered, writable: false);
            var detected = await SessionFileValidator.DetectAsync(
                file.Name, detectionStream, _options, ct);
            if (!detected.Allowed)
            {
                await FailAsync(file, expected, ct);
                throw new SessionFileException(detected.Code!);
            }

            if (!await _registry.TransitionAsync(actor.SessionId, file.Id, expected,
                    SessionFileState.Ready, detected.DetectedMimeType, buffered.Length, ct))
            {
                throw new SessionFileException("file_state_conflict");
            }
        }

        return await RequireFileAsync(actor.SessionId, file.Id, ct);
    }

    public Task<IReadOnlyList<SessionFileRecord>> ListAsync(
        SessionFileActor actor,
        CancellationToken ct = default) => _registry.ListAsync(actor.SessionId, ct);

    public async Task<FileContentResult> OpenContentAsync(
        SessionFileActor actor,
        string fileId,
        CancellationToken ct = default)
    {
        var file = await RequireReadyFileAsync(actor.SessionId, fileId, ct);
        var mimeType = file.DetectedMimeType ?? file.DeclaredMimeType;
        if (file.StorageKind == SessionFileStorageKind.S3)
        {
            // Redirecting is cheaper, but only works if the client can reach storage.
            if (_artifacts.CanServeBrowsersDirectly)
            {
                return new FileContentResult(
                    null,
                    _artifacts.PresignGet(
                        file.StorageLocator, TimeSpan.FromMinutes(_options.PresignMinutes)),
                    mimeType,
                    file.Name,
                    file.Size);
            }

            var objectStream = await _artifacts.OpenReadAsync(file.StorageLocator, ct)
                ?? throw new SessionFileException("file_content_expired");
            return new FileContentResult(objectStream, null, mimeType, file.Name, file.Size);
        }

        var liveSession = await TryGetLiveSessionAsync(actor.Owner, actor.SessionId, ct);
        if (liveSession is null)
        {
            await ExpireReadyPodFileAsync(file, ct);
            throw new SessionFileException("file_content_expired");
        }
        var stream = await _agentFiles.OpenReadAsync(liveSession, file, ct);
        if (stream is null)
        {
            await ExpireReadyPodFileAsync(file, ct);
            throw new SessionFileException("file_content_expired");
        }
        return new FileContentResult(stream, null, mimeType, file.Name, file.Size);
    }

    private async Task ExpireReadyPodFileAsync(SessionFileRecord file, CancellationToken ct)
    {
        await _registry.TransitionAsync(file.SessionId, file.Id, SessionFileState.Ready,
            SessionFileState.Expired, null, null, ct);
        await _registry.ClearPresentationIfFileAsync(file.SessionId, file.Id, "system", ct);
    }

    public async Task DeleteAsync(
        SessionFileActor actor,
        string fileId,
        CancellationToken ct = default)
    {
        if (!actor.CanManage)
        {
            throw new SessionFileException("file_access_denied");
        }

        var file = await RequireFileAsync(actor.SessionId, fileId, ct);
        if (!await _registry.TransitionAsync(actor.SessionId, file.Id, file.State,
                SessionFileState.Deleted, null, null, ct))
        {
            throw new SessionFileException("file_state_conflict");
        }
        await _registry.ClearPresentationIfFileAsync(file.SessionId, file.Id, actor.Principal, ct);
        try
        {
            if (file.StorageKind == SessionFileStorageKind.S3)
            {
                await _artifacts.DeleteAsync(file.StorageLocator, ct);
            }
            else
            {
                var session = await TryGetLiveSessionAsync(actor.Owner, actor.SessionId, ct);
                if (session is not null)
                {
                    await _agentFiles.DeleteAsync(session, file, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.LogWarning(exception, "Deferred cleanup required for session file {FileId}", file.Id);
        }
    }

    public Task<SessionFilePresentation?> GetPresentationAsync(
        SessionFileActor actor,
        CancellationToken ct = default) =>
        _registry.GetPresentationAsync(actor.SessionId, ct);

    public async Task<SessionFilePresentation> SetPresentationAsync(
        SessionFileActor actor,
        string? fileId,
        CancellationToken ct = default)
    {
        RequireWrite(actor);
        if (fileId is not null)
        {
            await RequireReadyFileAsync(actor.SessionId, fileId, ct);
        }

        return await _registry.SetPresentationAsync(
            actor.SessionId, fileId, actor.Principal, ct);
    }

    public async Task DeleteSessionFilesAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var files = await _registry.ListAsync(sessionId, ct);
        foreach (var file in files)
        {
            if (file.StorageKind == SessionFileStorageKind.S3)
            {
                await _artifacts.DeleteAsync(file.StorageLocator, ct);
            }
            else
            {
                var session = await TryGetLiveSessionAsync(file.Owner, sessionId, ct);
                if (session is not null)
                {
                    await _agentFiles.DeleteAsync(session, file, ct);
                }
            }
        }

        await _registry.MarkSessionDeletedAsync(sessionId, ct);
    }

    private async Task<SessionInfo> GetLiveSessionAsync(
        SessionFileActor actor,
        CancellationToken ct)
    {
        return await TryGetLiveSessionAsync(actor.Owner, actor.SessionId, ct)
            ?? throw new SessionFileException("temporary_storage_unavailable");
    }

    private async Task<SessionInfo?> TryGetLiveSessionAsync(
        string owner,
        string sessionId,
        CancellationToken ct)
    {
        var session = await _sessions.GetSessionAsync(owner, sessionId, ct);
        return session is { Phase: "Running", PodIp: not null } ? session : null;
    }

    private async Task<SessionFileRecord> RequireFileAsync(
        string sessionId,
        string fileId,
        CancellationToken ct) =>
        await _registry.GetAsync(sessionId, fileId, ct)
            ?? throw new SessionFileException("file_not_found");

    private async Task<SessionFileRecord> RequireReadyFileAsync(
        string sessionId,
        string fileId,
        CancellationToken ct)
    {
        var file = await RequireFileAsync(sessionId, fileId, ct);
        if (file.State != SessionFileState.Ready)
        {
            throw new SessionFileException("file_not_ready");
        }

        return file;
    }

    private Task FailAsync(
        SessionFileRecord file,
        SessionFileState expected,
        CancellationToken ct) =>
        _registry.TransitionAsync(file.SessionId, file.Id, expected,
            SessionFileState.Failed, null, null, ct);

    private static void RequireWrite(SessionFileActor actor)
    {
        if (!actor.CanWrite)
        {
            throw new SessionFileException("file_access_denied");
        }
    }

    private static bool IsOffice(string extension) =>
        extension is ".docx" or ".pptx" or ".xlsx";

    private static async Task<byte[]?> BufferAsync(
        Stream source,
        long maximumBytes,
        CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                return output.ToArray();
            }

            total += read;
            if (total > maximumBytes)
            {
                return null;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private sealed class SizeLimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            Count(read);
            return read;
        }
        /// <summary>
        /// The array overload has to be overridden too. The AWS SDK reads through it, and
        /// <see cref="Stream"/>'s default implementation of it routes to the synchronous
        /// <see cref="Read(byte[],int,int)"/> above — which on a Kestrel request body throws
        /// "Synchronous operations are disallowed", failing the upload after the request was
        /// already accepted. Overriding only the Memory version is not enough.
        /// </summary>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
        private void Count(int read)
        {
            _read += read;
            if (_read > maximumBytes)
            {
                throw new InvalidDataException("The upload exceeds its reserved size.");
            }
        }
    }
}
