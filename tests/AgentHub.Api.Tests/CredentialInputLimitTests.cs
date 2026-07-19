using System.Text;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public class CredentialInputLimitTests
{
    [Fact]
    public async Task ProviderCredentials_RejectsDeclaredBodyLargerThan64KiBWithoutReadingIt()
    {
        var body = new TrackingStream(Encoding.UTF8.GetBytes("{}"));
        var service = new RecordingSessionService();
        var controller = Controller(body, ProviderCredentialValidator.MaxBytes + 1, service);

        var result = await controller.ProviderCredentials("session-1", "codex", CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        Assert.Equal(0, body.BytesRead);
        Assert.Equal(0, service.StoreCalls);
    }

    [Fact]
    public async Task ProviderCredentials_RejectsUnknownLengthBodyAfterReadingAtMost64KiBPlusOneByte()
    {
        var body = new TrackingStream(new byte[ProviderCredentialValidator.MaxBytes + 100]);
        var service = new RecordingSessionService();
        var controller = Controller(body, null, service);

        var result = await controller.ProviderCredentials("session-1", "codex", CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        Assert.Equal(ProviderCredentialValidator.MaxBytes + 1, body.BytesRead);
        Assert.Equal(0, service.StoreCalls);
    }

    private static InternalController Controller(Stream body, long? contentLength, RecordingSessionService service)
    {
        var session = new SessionRecord
        {
            Id = "session-1", Owner = "alice", CallbackToken = "callback-token",
            Agent = AgentKind.Codex, AuthMode = AgentAuthMode.Subscription
        };
        var controller = new InternalController(new CallbackSessionStore(session), [], service, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Headers["X-Agent-Token"] = session.CallbackToken;
        controller.Request.ContentLength = contentLength;
        controller.Request.Body = body;
        return controller;
    }

    private sealed class TrackingStream(byte[] data) : Stream
    {
        private int _position;
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public int StoreCalls { get; private set; }
        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => Task.FromResult(new CredentialStatus());
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) { StoreCalls++; return Task.CompletedTask; }
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
}
