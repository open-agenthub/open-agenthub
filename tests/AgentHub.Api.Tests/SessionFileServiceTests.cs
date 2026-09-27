using System.Text;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionFileServiceTests
{
    private static readonly SessionFileActor Actor =
        new("s1", "alice", "alice", CanWrite: true, CanManage: true);

    [Fact]
    public async Task Reserve_uses_pod_only_when_s3_is_not_configured_and_pod_is_live()
    {
        var harness = Harness(s3Configured: false, podPhase: "Running");

        var result = await harness.Service.ReserveAsync(Actor,
            new ReserveSessionFileCommand("shot.png", "image/png", 8, "batch-1", "user"));

        Assert.Equal(SessionFileStorageKind.Pod, result.File.StorageKind);
        Assert.Equal("proxy", result.Upload.Kind);
        Assert.Single(harness.Registry.Files);
    }

    [Fact]
    public async Task Reserve_uses_s3_without_requiring_a_live_pod()
    {
        var harness = Harness(s3Configured: true, podPhase: "Paused");

        var result = await harness.Service.ReserveAsync(Actor,
            new ReserveSessionFileCommand("shot.png", "image/png", 8, null, "user"));

        // Storage is only reachable from inside the cluster, so the bytes have to come
        // through the API — a presigned URL would point at an address the browser cannot
        // resolve.
        Assert.Equal(SessionFileStorageKind.S3, result.File.StorageKind);
        Assert.Equal("proxy", result.Upload.Kind);
        Assert.StartsWith("/api/sessions/", result.Upload.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reserve_presigns_when_storage_is_reachable_from_the_browser()
    {
        var harness = Harness(s3Configured: true, podPhase: "Paused");
        harness.Artifacts.CanServeBrowsersDirectly = true;

        var result = await harness.Service.ReserveAsync(Actor,
            new ReserveSessionFileCommand("shot.png", "image/png", 8, null, "user"));

        Assert.Equal("presigned", result.Upload.Kind);
        Assert.StartsWith("https://storage.test/", result.Upload.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxied_upload_of_an_s3_file_stores_the_content_without_a_live_pod()
    {
        var harness = Harness(s3Configured: true, podPhase: "Paused");
        var file = Reserved("f1", SessionFileStorageKind.S3);
        await harness.Registry.InsertAsync(file);

        await harness.Service.PutPodContentAsync(Actor, "f1", new MemoryStream([1, 2, 3, 4]));

        Assert.Equal([1, 2, 3, 4], harness.Artifacts.Objects[file.StorageLocator]);
        Assert.Equal(SessionFileState.Uploading, harness.Registry.Files["f1"].State);
    }

    [Fact]
    public async Task Proxied_upload_passes_the_wire_length_for_an_unseekable_request_body()
    {
        var harness = Harness(s3Configured: true, podPhase: "Paused");
        var file = Reserved("f1", SessionFileStorageKind.S3);
        await harness.Registry.InsertAsync(file);
        // A Kestrel request body reports neither Length nor CanSeek; without the wire length
        // the S3 SDK fails with "Could not determine content length".
        await using var body = new UnseekableStream([1, 2, 3, 4]);

        await harness.Service.PutPodContentAsync(Actor, "f1", body, 4);

        Assert.Equal([1, 2, 3, 4], harness.Artifacts.Objects[file.StorageLocator]);
        Assert.Equal(4, harness.Artifacts.PutContentLengths[file.StorageLocator]);
    }

    private sealed class UnseekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        // Kestrel refuses synchronous reads of a request body, so this does too. Allowing it
        // here is what let a wrapper that falls back to synchronous Read pass the tests and
        // still fail every real upload.
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Reserve_without_s3_or_a_live_pod_does_not_create_a_row()
    {
        var harness = Harness(s3Configured: false, podPhase: "Paused");

        var error = await Assert.ThrowsAsync<SessionFileException>(() =>
            harness.Service.ReserveAsync(Actor,
                new ReserveSessionFileCommand("shot.png", "image/png", 8, null, "user")));

        Assert.Equal("temporary_storage_unavailable", error.Code);
        Assert.Empty(harness.Registry.Files);
    }

    [Fact]
    public async Task S3_completion_failure_does_not_switch_the_record_to_pod()
    {
        var harness = Harness(s3Configured: true, podPhase: "Running");
        await harness.Registry.InsertAsync(Reserved("f1", SessionFileStorageKind.S3));

        var error = await Assert.ThrowsAsync<SessionFileException>(
            () => harness.Service.CompleteAsync(Actor, "f1"));

        Assert.Equal("storage_verification_failed", error.Code);
        Assert.Empty(harness.Agent.Uploads);
        Assert.Equal(SessionFileState.Failed, harness.Registry.Files["f1"].State);
    }

    [Fact]
    public async Task Pod_upload_is_detected_and_transitions_to_ready()
    {
        var harness = Harness(s3Configured: false, podPhase: "Running");
        var reserved = await harness.Service.ReserveAsync(Actor,
            new ReserveSessionFileCommand("shot.png", "image/png", 8, null, "user"));
        await using var content = new MemoryStream(
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

        await harness.Service.PutPodContentAsync(Actor, reserved.File.Id, content);
        var completed = await harness.Service.CompleteAsync(Actor, reserved.File.Id);

        Assert.Equal(SessionFileState.Ready, completed.State);
        Assert.Equal("image/png", completed.DetectedMimeType);
    }

    [Fact]
    public async Task Completion_rejects_content_that_does_not_match_the_declared_image()
    {
        var harness = Harness(s3Configured: true, podPhase: "Running");
        var file = Reserved("f1", SessionFileStorageKind.S3, size: 8);
        await harness.Registry.InsertAsync(file);
        harness.Artifacts.Objects[file.StorageLocator] = Encoding.ASCII.GetBytes("%PDF-1.7");

        var error = await Assert.ThrowsAsync<SessionFileException>(
            () => harness.Service.CompleteAsync(Actor, "f1"));

        Assert.Equal("content_type_mismatch", error.Code);
        Assert.Equal(SessionFileState.Failed, harness.Registry.Files["f1"].State);
    }

    [Fact]
    public async Task Reserve_enforces_the_session_file_count_quota()
    {
        var harness = Harness(s3Configured: true, podPhase: "Running",
            options: new SessionFileOptions { MaxSessionFiles = 1 });
        await harness.Registry.InsertAsync(Reserved("existing", SessionFileStorageKind.S3) with
        {
            State = SessionFileState.Ready,
            DetectedMimeType = "image/png",
        });

        var error = await Assert.ThrowsAsync<SessionFileException>(() =>
            harness.Service.ReserveAsync(Actor,
                new ReserveSessionFileCommand("second.png", "image/png", 8, null, "user")));

        Assert.Equal("session_file_count_exceeded", error.Code);
    }

    [Fact]
    public async Task S3_read_returns_a_redirect_and_manager_delete_marks_the_file_deleted()
    {
        var harness = Harness(s3Configured: true, podPhase: "Paused");
        harness.Artifacts.CanServeBrowsersDirectly = true;
        var file = Reserved("f1", SessionFileStorageKind.S3) with
        {
            State = SessionFileState.Ready,
            DetectedMimeType = "image/png",
        };
        await harness.Registry.InsertAsync(file);

        var opened = await harness.Service.OpenContentAsync(Actor, "f1");
        await harness.Service.DeleteAsync(Actor, "f1");

        Assert.Null(opened.Content);
        Assert.StartsWith("https://storage.test/", opened.RedirectUrl, StringComparison.Ordinal);
        Assert.Equal(SessionFileState.Deleted, harness.Registry.Files["f1"].State);
        Assert.Contains(file.StorageLocator, harness.Artifacts.Deleted);
    }

    [Fact]
    public async Task S3_read_streams_through_the_api_when_storage_is_internal_only()
    {
        var harness = Harness(s3Configured: true, podPhase: "Paused");
        var file = Reserved("f1", SessionFileStorageKind.S3) with
        {
            State = SessionFileState.Ready,
            DetectedMimeType = "image/png",
        };
        await harness.Registry.InsertAsync(file);
        harness.Artifacts.Objects[file.StorageLocator] = [7, 7, 7];

        var opened = await harness.Service.OpenContentAsync(Actor, "f1");

        Assert.Null(opened.RedirectUrl);
        Assert.NotNull(opened.Content);
        using var buffer = new MemoryStream();
        await opened.Content!.CopyToAsync(buffer);
        Assert.Equal([7, 7, 7], buffer.ToArray());
    }

    [Fact]
    public async Task Delete_marks_metadata_first_and_tolerates_storage_cleanup_failure()
    {
        var harness = Harness(s3Configured: true, podPhase: "Paused");
        var file = Reserved("f1", SessionFileStorageKind.S3) with
        {
            State = SessionFileState.Ready,
            DetectedMimeType = "image/png",
        };
        await harness.Registry.InsertAsync(file);
        await harness.Registry.SetPresentationAsync("s1", "f1", "alice");
        harness.Artifacts.FailDelete = true;

        await harness.Service.DeleteAsync(Actor, "f1");

        Assert.Equal(SessionFileState.Deleted, harness.Registry.Files["f1"].State);
        Assert.Null((await harness.Registry.GetPresentationAsync("s1"))!.FileId);
    }

    [Fact]
    public async Task Pod_file_expires_and_returns_gone_when_the_pod_is_unavailable()
    {
        var harness = Harness(s3Configured: false, podPhase: "Paused");
        var file = Reserved("f1", SessionFileStorageKind.Pod) with
        {
            State = SessionFileState.Ready,
            DetectedMimeType = "image/png",
        };
        await harness.Registry.InsertAsync(file);
        await harness.Registry.SetPresentationAsync("s1", "f1", "alice");

        var error = await Assert.ThrowsAsync<SessionFileException>(
            () => harness.Service.OpenContentAsync(Actor, "f1"));

        Assert.Equal("file_content_expired", error.Code);
        Assert.Equal(SessionFileState.Expired, harness.Registry.Files["f1"].State);
        Assert.Null((await harness.Registry.GetPresentationAsync("s1"))!.FileId);
    }

    [Fact]
    public async Task Presentation_rejects_a_read_only_actor_and_increments_for_a_writer()
    {
        var harness = Harness(s3Configured: true, podPhase: "Running");
        var file = Reserved("f1", SessionFileStorageKind.S3) with
        {
            State = SessionFileState.Ready,
            DetectedMimeType = "image/png",
        };
        await harness.Registry.InsertAsync(file);
        var readOnly = Actor with { CanWrite = false, CanManage = false, Principal = "viewer" };

        await Assert.ThrowsAsync<SessionFileException>(() =>
            harness.Service.SetPresentationAsync(readOnly, "f1"));
        var first = await harness.Service.SetPresentationAsync(Actor, "f1");
        var second = await harness.Service.SetPresentationAsync(Actor, null);

        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
    }

    private static TestHarness Harness(
        bool s3Configured,
        string podPhase,
        SessionFileOptions? options = null)
    {
        var registry = new MemoryRegistry();
        var artifacts = new MemoryArtifacts(s3Configured);
        var agent = new MemoryAgentFiles();
        var sessions = new SessionLookup(podPhase);
        return new TestHarness(
            new SessionFileService(registry, artifacts, agent, sessions, options ?? new()),
            registry, artifacts, agent);
    }

    private static SessionFileRecord Reserved(
        string id,
        SessionFileStorageKind storage,
        long size = 8) => new(
            id, "s1", "alice", "shot.png", ".png", "image/png", null, size,
            storage, $"sessions/alice/s1/files/{id}/shot.png", SessionFileState.Reserved,
            SessionFilePreviewState.None, null, "alice", "user", DateTime.UtcNow,
            null, null);

    private sealed record TestHarness(
        SessionFileService Service,
        MemoryRegistry Registry,
        MemoryArtifacts Artifacts,
        MemoryAgentFiles Agent);

    private sealed class MemoryRegistry : ISessionFileRegistry
    {
        public Dictionary<string, SessionFileRecord> Files { get; } = new();
        private SessionFilePresentation? _presentation;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertAsync(SessionFileRecord file, CancellationToken ct = default)
        {
            Files.Add(file.Id, file);
            return Task.CompletedTask;
        }
        public Task<SessionFileRecord?> GetAsync(string sessionId, string fileId, CancellationToken ct = default) =>
            Task.FromResult(Files.GetValueOrDefault(fileId) is { } file && file.SessionId == sessionId ? file : null);
        public Task<IReadOnlyList<SessionFileRecord>> ListAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionFileRecord>>(Files.Values
                .Where(f => f.SessionId == sessionId && f.State is not SessionFileState.Deleted and not SessionFileState.Expired)
                .ToArray());
        public Task<SessionFileUsage> GetUsageAsync(string sessionId, CancellationToken ct = default)
        {
            var ready = Files.Values.Where(f => f.SessionId == sessionId && f.State == SessionFileState.Ready).ToArray();
            return Task.FromResult(new SessionFileUsage(ready.Length, ready.Sum(f => f.Size)));
        }
        public Task<bool> TransitionAsync(string sessionId, string fileId, SessionFileState expected,
            SessionFileState next, string? detectedMime, long? actualSize, CancellationToken ct = default)
        {
            if (!Files.TryGetValue(fileId, out var file) || file.SessionId != sessionId || file.State != expected)
                return Task.FromResult(false);
            Files[fileId] = file with
            {
                State = next,
                DetectedMimeType = detectedMime ?? file.DetectedMimeType,
                Size = actualSize ?? file.Size,
                CompletedAt = next == SessionFileState.Ready ? DateTime.UtcNow : file.CompletedAt,
            };
            return Task.FromResult(true);
        }
        public Task<SessionFilePresentation?> GetPresentationAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_presentation?.SessionId == sessionId ? _presentation : null);
        public Task<SessionFilePresentation> SetPresentationAsync(string sessionId, string? fileId,
            string presenter, CancellationToken ct = default)
        {
            _presentation = new(sessionId, fileId, (_presentation?.Revision ?? 0) + 1,
                presenter, DateTime.UtcNow);
            return Task.FromResult(_presentation);
        }
        public Task<SessionFileRecord?> ClaimPreviewAsync(CancellationToken ct = default) => Task.FromResult<SessionFileRecord?>(null);
        public Task LinkPreviewAsync(string sourceId, string previewId, bool succeeded, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionFileRecord>> ExpireReservationsAsync(DateTime cutoff, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionFileRecord>>(Array.Empty<SessionFileRecord>());
        public Task MarkSessionDeletedAsync(string sessionId, CancellationToken ct = default)
        {
            foreach (var file in Files.Values.Where(f => f.SessionId == sessionId).ToArray())
                Files[file.Id] = file with { State = SessionFileState.Deleted };
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryArtifacts(bool configured) : IArtifactStore
    {
        public bool IsConfigured => configured;
        public bool CanServeBrowsersDirectly { get; set; }
        public async Task<bool> TryPutStreamAsync(
            string key,
            Stream content,
            string? contentType,
            long? contentLength = null,
            CancellationToken ct = default)
        {
            // Mirrors the S3 SDK, which refuses a body whose length it cannot determine.
            if (contentLength is null && !content.CanSeek)
                throw new InvalidOperationException("Could not determine content length");
            PutContentLengths[key] = contentLength;
            using var buffer = new MemoryStream();
            // Reads through the byte[] overload on purpose, because that is the one the AWS SDK
            // uses. CopyToAsync goes through the Memory overload instead, so it never exercised
            // the path that actually broke uploads.
            var chunk = new byte[512];
            int read;
            while ((read = await content.ReadAsync(chunk, 0, chunk.Length, ct)) > 0)
                buffer.Write(chunk, 0, read);
            Objects[key] = buffer.ToArray();
            return true;
        }
        public Dictionary<string, long?> PutContentLengths { get; } = new();
        public Dictionary<string, byte[]> Objects { get; } = new();
        public List<string> Deleted { get; } = new();
        public bool FailDelete { get; set; }
        public string PresignPut(string key, TimeSpan ttl) => $"https://storage.test/{key}?put";
        public string PresignGet(string key, TimeSpan ttl) => $"https://storage.test/{key}?get";
        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<ArtifactObjectInfo?> HeadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Objects.TryGetValue(key, out var bytes) ? new ArtifactObjectInfo(bytes.Length, null) : null);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            if (FailDelete) throw new IOException("storage unavailable");
            Deleted.Add(key);
            Objects.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryAgentFiles : IAgentFileClient
    {
        public Dictionary<string, byte[]> Uploads { get; } = new();
        public async Task PutAsync(SessionInfo session, SessionFileRecord file, Stream content, CancellationToken ct)
        {
            using var output = new MemoryStream();
            await content.CopyToAsync(output, ct);
            Uploads[file.Id] = output.ToArray();
        }
        public Task<Stream?> OpenReadAsync(SessionInfo session, SessionFileRecord file, CancellationToken ct) =>
            Task.FromResult<Stream?>(Uploads.TryGetValue(file.Id, out var bytes) ? new MemoryStream(bytes) : null);
        public Task<bool> ExistsAsync(SessionInfo session, SessionFileRecord file, CancellationToken ct) =>
            Task.FromResult(Uploads.ContainsKey(file.Id));
        public Task DeleteAsync(SessionInfo session, SessionFileRecord file, CancellationToken ct)
        {
            Uploads.Remove(file.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class SessionLookup(string phase) : ISessionService
    {
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult<SessionInfo?>(new SessionInfo
            {
                Id = id, Owner = owner, Title = id, Mode = SessionMode.Interactive,
                Phase = phase, PodIp = phase == "Running" ? "10.0.0.8" : null,
            });
        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
