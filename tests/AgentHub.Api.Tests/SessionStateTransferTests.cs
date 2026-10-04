using System.Security.Claims;
using System.Text;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Download and upload of a session's provider state archive — the path that lets a session
/// continue on a workstation and come back. The service double reproduces the production
/// decisions (owner scoping, the live-pod guard through the real SessionStatus predicate, the
/// state key through the real IArtifactStore) so the test cannot pass on a fake that is merely
/// more permissive than the cluster.
/// </summary>
public class SessionStateTransferTests
{
    private const string Owner = "alice";
    private const string SessionId = "session-1";

    [Fact]
    public async Task DownloadState_StreamsTheStoredArchive()
    {
        var artifacts = new MemoryArtifacts();
        artifacts.Objects[StateKey(AgentKind.Claude)] = Gzip("conversation");
        var controller = Controller(SessionStatus.Paused, artifacts);

        var result = await controller.DownloadState(SessionId, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/gzip", file.ContentType);
        Assert.Equal($"{SessionId}-state.tgz", file.FileDownloadName);
        using var buffer = new MemoryStream();
        await file.FileStream.CopyToAsync(buffer);
        Assert.Equal(Gzip("conversation"), buffer.ToArray());
    }

    [Fact]
    public async Task DownloadState_ReadsThePerProviderKey()
    {
        // The pod of a Codex session writes codex-state.tgz. Reading the Claude key for every
        // agent would hand back another provider's archive, or nothing at all.
        var artifacts = new MemoryArtifacts();
        artifacts.Objects[StateKey(AgentKind.Codex)] = Gzip("codex");
        var controller = Controller(SessionStatus.Paused, artifacts, AgentKind.Codex);

        var file = Assert.IsType<FileStreamResult>(
            await controller.DownloadState(SessionId, CancellationToken.None));

        using var buffer = new MemoryStream();
        await file.FileStream.CopyToAsync(buffer);
        Assert.Equal(Gzip("codex"), buffer.ToArray());
    }

    [Fact]
    public async Task DownloadState_NotFoundForForeignSessionAndForNothingStored()
    {
        var artifacts = new MemoryArtifacts();
        artifacts.Objects[StateKey(AgentKind.Claude)] = Gzip("conversation");
        var controller = Controller(SessionStatus.Paused, artifacts);

        Assert.IsType<NotFoundResult>(await controller.DownloadState("not-mine", CancellationToken.None));
        // Owned, but never ran: the key does not exist yet.
        Assert.IsType<NotFoundResult>(
            await Controller(SessionStatus.Pending, new MemoryArtifacts())
                .DownloadState(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task UploadState_StoresTheArchiveUnderTheSessionKey()
    {
        var artifacts = new MemoryArtifacts();
        var payload = Gzip("continued locally");
        var controller = Controller(SessionStatus.Paused, artifacts, body: payload);

        var result = await controller.UploadState(SessionId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(payload, artifacts.Objects[StateKey(AgentKind.Claude)]);
        Assert.Equal("application/gzip", artifacts.ContentTypes[StateKey(AgentKind.Claude)]);
    }

    [Fact]
    public async Task UploadState_OverwritesWhatTheSessionItselfStored()
    {
        // The whole point of the upload: the next resume has to unpack the archive that came
        // from outside, not the one the pod left behind.
        var artifacts = new MemoryArtifacts();
        artifacts.Objects[StateKey(AgentKind.Claude)] = Gzip("from the pod");
        var controller = Controller(SessionStatus.Paused, artifacts, body: Gzip("from the laptop"));

        await controller.UploadState(SessionId, CancellationToken.None);

        Assert.Equal(Gzip("from the laptop"), artifacts.Objects[StateKey(AgentKind.Claude)]);
    }

    [Theory]
    [InlineData(SessionStatus.Running)]
    [InlineData(SessionStatus.Pending)]
    public async Task UploadState_RefusedWhileAPodIsLive(string phase)
    {
        var artifacts = new MemoryArtifacts();
        artifacts.Objects[StateKey(AgentKind.Claude)] = Gzip("from the pod");
        var controller = Controller(phase, artifacts, body: Gzip("from the laptop"));

        var result = await controller.UploadState(SessionId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        // Rejected means untouched: a 409 that had already replaced the bytes would be worse
        // than either outcome alone.
        Assert.Equal(Gzip("from the pod"), artifacts.Objects[StateKey(AgentKind.Claude)]);
    }

    [Fact]
    public async Task UploadState_AcceptsAChunkedBodyWithoutAContentLength()
    {
        // A client that streams a tar.gz sends no Content-Length. S3ArtifactStore buffers to
        // learn the length; nothing in this path may require the header.
        var artifacts = new MemoryArtifacts();
        var controller = Controller(SessionStatus.Paused, artifacts, body: Gzip("streamed"));
        controller.Request.ContentLength = null;

        Assert.IsType<NoContentResult>(await controller.UploadState(SessionId, CancellationToken.None));
        Assert.Equal(Gzip("streamed"), artifacts.Objects[StateKey(AgentKind.Claude)]);
    }

    [Fact]
    public async Task UploadState_NotFoundForAForeignSession()
        => Assert.IsType<NotFoundResult>(await Controller(SessionStatus.Paused, new MemoryArtifacts())
            .UploadState("not-mine", CancellationToken.None));

    [Fact]
    public async Task UploadState_ReportsUnavailableWithoutObjectStorage()
    {
        // NullArtifactStore keeps nothing. Answering 204 there would promise a resume that
        // then starts from an empty home directory.
        var controller = Controller(SessionStatus.Paused, new NullArtifactStore(), body: Gzip("x"));

        var result = Assert.IsType<ObjectResult>(await controller.UploadState(SessionId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    // ------------------------------------------------------------------ fixtures

    private static string StateKey(AgentKind agent) =>
        IArtifactStore.StateKey(OwnerKey, SessionId, agent);

    /// <summary>The service hashes the owner before it touches a key; the test has to use the
    /// same transformation or it would assert against a key production never writes.</summary>
    private static string OwnerKey => StateTransferService.OwnerKey(Owner);

    private static byte[] Gzip(string content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(content));
        return buffer.ToArray();
    }

    private static SessionsController Controller(
        string phase, IArtifactStore artifacts, AgentKind agent = AgentKind.Claude, byte[]? body = null)
    {
        var controller = new SessionsController(
            new StateTransferService(Owner, SessionId, phase, agent, artifacts), null!, [])
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("preferred_username", Owner)], "test"));
        if (body is not null)
        {
            // Deliberately not a MemoryStream: a Kestrel request body cannot seek and does not
            // know its own length, which is what broke earlier uploads through this layer.
            controller.Request.Body = new UnseekableStream(body);
            controller.Request.ContentLength = body.Length;
        }
        return controller;
    }

    /// <summary>
    /// Reproduces KubernetesSessionService's two state methods: the session is looked up by
    /// owner, the key is derived from the hashed owner plus the agent, and the upload runs the
    /// real live-pod predicate before handing bytes to the real store interface.
    /// </summary>
    private sealed class StateTransferService(
        string owner, string sessionId, string phase, AgentKind agent, IArtifactStore artifacts)
        : ISessionService
    {
        public static string OwnerKey(string owner)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(owner)))[..16].ToLowerInvariant();
            return $"u-{hash}";
        }

        private string Key => IArtifactStore.StateKey(OwnerKey(owner), sessionId, agent);

        public Task<Stream?> OpenStateArchiveAsync(string o, string id, CancellationToken ct = default)
            => o == owner && id == sessionId
                ? artifacts.OpenReadAsync(Key, ct)
                : Task.FromResult<Stream?>(null);

        public Task<bool> ReplaceStateArchiveAsync(string o, string id, Stream content,
            long? contentLength, CancellationToken ct = default)
        {
            if (o != owner || id != sessionId) throw new KeyNotFoundException($"Session {id} not found.");
            if (!SessionStatus.CanReplaceState(phase))
                throw new InvalidOperationException("Pause the session before uploading its state.");
            return artifacts.TryPutStreamAsync(Key, content, "application/gzip", contentLength, ct);
        }

        public Task StoreCredentialsAsync(string o, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string o, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string o, AgentKind a, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string o, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string o, string id, DuplicateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string o, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string o, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string id, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>In-memory object storage that mirrors S3ArtifactStore: the length may be absent,
    /// in which case the bytes are buffered to learn it rather than rejected.</summary>
    private sealed class MemoryArtifacts : IArtifactStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public Dictionary<string, string?> ContentTypes { get; } = new();

        public async Task<bool> TryPutStreamAsync(string key, Stream content, string? contentType,
            long? contentLength = null, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[512];
            int read;
            while ((read = await content.ReadAsync(chunk, 0, chunk.Length, ct)) > 0)
                buffer.Write(chunk, 0, read);
            var bytes = buffer.ToArray();
            if (contentLength is { } declared && declared != bytes.Length)
                throw new InvalidOperationException(
                    $"Declared {declared} bytes, received {bytes.Length} — S3 rejects such a request.");
            Objects[key] = bytes;
            ContentTypes[key] = contentType;
            return true;
        }

        public string PresignPut(string key, TimeSpan ttl) => $"https://storage.test/{key}?put";
        public string PresignGet(string key, TimeSpan ttl) => $"https://storage.test/{key}?get";
        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>A request body: forward-only, no length.</summary>
    private sealed class UnseekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
