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

    private static InternalSessionFilesController Controller(
        IAgentCallbackAuthorizer authorizer,
        RecordingFiles files) => new(
            authorizer, files, new NullArtifactStore(), new SessionFileOptions());

    private static SessionRecord Session() => new()
    {
        Id = "s1", Owner = "alice", CallbackToken = "secret",
    };

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
        public Task PutPodContentAsync(SessionFileActor actor, string fileId, Stream content, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionFileRecord> CompleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => Task.FromResult(File());
        public Task<AgentHub.Api.Files.FileContentResult> OpenContentAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation?> GetPresentationAsync(SessionFileActor actor, CancellationToken ct = default) => Task.FromResult<SessionFilePresentation?>(null);
        public Task<SessionFilePresentation> SetPresentationAsync(SessionFileActor actor, string? fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
