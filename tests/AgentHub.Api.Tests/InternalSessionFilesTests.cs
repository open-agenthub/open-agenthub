using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class InternalSessionFilesTests
{
    [Fact]
    public async Task Callback_token_is_scoped_to_its_session()
    {
        var controller = Controller(new FixedAuthorizer(null), new RecordingFiles());

        Assert.IsType<UnauthorizedResult>(await controller.List("other", default));
    }

    [Fact]
    public async Task Reserve_rewrites_pod_upload_to_the_internal_token_route()
    {
        var files = new RecordingFiles();
        var controller = Controller(new FixedAuthorizer(Session()), files);

        var result = Assert.IsType<OkObjectResult>(await controller.Reserve(
            "s1", new ReserveSessionFileRequest("out.png", "image/png", 3, null), default));
        var payload = Assert.IsType<InternalReserveFileResponse>(result.Value);

        Assert.Equal("agent", files.LastActor!.Principal);
        Assert.Equal("agent", files.LastCommand!.Source);
        Assert.Equal("proxy", payload.Upload.Kind);
        Assert.Equal("/internal/sessions/s1/files/f1/content", payload.Upload.Url);
    }

    [Fact]
    public async Task Materialize_returns_only_ready_files_from_the_callback_session()
    {
        var files = new RecordingFiles();
        var controller = Controller(new FixedAuthorizer(Session()), files);

        var result = Assert.IsType<OkObjectResult>(await controller.Materialize(
            "s1", new MaterializeSessionFilesRequest(["f1"]), default));
        var payload = Assert.IsAssignableFrom<IReadOnlyList<MaterializedSessionFile>>(result.Value);

        Assert.Single(payload);
        Assert.Equal("f1/out.png", payload[0].Locator);
        Assert.Null(payload[0].DownloadUrl);
    }

    [Fact]
    public async Task Content_streams_the_bytes_and_never_redirects_the_agent()
    {
        // This route exists so a pod that cannot reach object storage can still read its files. It
        // had no test at all, and a redirect here is silently wrong rather than visibly broken: the
        // agent's fetch follows it, so it looks like it works wherever storage happens to be
        // reachable from the pod.
        var files = new RecordingFiles();
        var controller = Controller(new FixedAuthorizer(Session()), files);

        var result = Assert.IsType<FileStreamResult>(await controller.Content("s1", "f1", default));

        Assert.False(files.LastAllowRedirect);
        Assert.Equal("image/png", result.ContentType);
        using var buffer = new MemoryStream();
        await result.FileStream.CopyToAsync(buffer);
        Assert.Equal([1, 2, 3], buffer.ToArray());
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions);
    }

    [Fact]
    public async Task Content_requires_the_session_s_own_callback_token()
    {
        var controller = Controller(new FixedAuthorizer(null), new RecordingFiles());

        Assert.IsType<UnauthorizedResult>(await controller.Content("s1", "f1", default));
    }

    private static InternalSessionFilesController Controller(
        IAgentCallbackAuthorizer authorizer,
        RecordingFiles files) => new(
            authorizer, files, new NullArtifactStore(), new SessionFileOptions(),
            new ProjectFileAccess(new NoSessions(), new NeverShared()))
        {
            // Content writes response headers, so it needs a real HttpContext to write them to.
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

    private static SessionRecord Session() => new()
    {
        Id = "s1", Owner = "alice", CallbackToken = "secret",
    };

    private sealed class NeverShared : AgentHub.Api.Ee.Sharing.ISessionShareStatus
    {
        public Task<bool> IsSharedAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NoSessions : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedAuthorizer(SessionRecord? record) : IAgentCallbackAuthorizer
    {
        public Task<SessionRecord?> AuthorizeAsync(
            HttpRequest request, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(record is not null && record.Id == sessionId ? record : null);
    }

    private sealed class RecordingFiles : ISessionFileService
    {
        public SessionFileActor? LastActor { get; private set; }
        public ReserveSessionFileCommand? LastCommand { get; private set; }
        private static SessionFileRecord File() => new(
            "f1", "s1", "alice", "out.png", ".png", "image/png", "image/png", 3,
            SessionFileStorageKind.Pod, "f1/out.png", SessionFileState.Ready,
            SessionFilePreviewState.None, null, "agent", "agent", DateTime.UtcNow,
            DateTime.UtcNow, null);

        public Task<ReserveFileResult> ReserveAsync(SessionFileActor actor, ReserveSessionFileCommand request, CancellationToken ct = default)
        {
            LastActor = actor; LastCommand = request;
            return Task.FromResult(new ReserveFileResult(File(),
                new FileUploadDescriptor("proxy", "/api/sessions/s1/files/f1/content", new Dictionary<string, string>())));
        }
        public Task<IReadOnlyList<SessionFileRecord>> ListAsync(SessionFileActor actor, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionFileRecord>>([File()]);
        public Task PutPodContentAsync(SessionFileActor actor, string fileId, Stream content, long? contentLength = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionFileRecord> CompleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => Task.FromResult(File());
        public bool? LastAllowRedirect { get; private set; }
        public Task<AgentHub.Api.Files.FileContentResult> OpenContentAsync(SessionFileActor actor, string fileId, bool allowRedirect = true, CancellationToken ct = default)
        {
            LastActor = actor; LastAllowRedirect = allowRedirect;
            return Task.FromResult(new AgentHub.Api.Files.FileContentResult(
                new MemoryStream([1, 2, 3]), null, "image/png", "out.png", 3));
        }
        public Task DeleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation?> GetPresentationAsync(SessionFileActor actor, CancellationToken ct = default) => Task.FromResult<SessionFilePresentation?>(null);
        public Task<SessionFilePresentation> SetPresentationAsync(SessionFileActor actor, string? fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
