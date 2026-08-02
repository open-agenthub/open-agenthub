using System.Reflection;
using System.Security.Claims;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Microsoft.AspNetCore.Mvc.Routing;

namespace AgentHub.Api.Tests;

public sealed class SessionFileAccessTests
{
    [Theory]
    [InlineData(SessionAccessLevel.Owner, true, true, true)]
    [InlineData(SessionAccessLevel.Collaborator, true, true, false)]
    [InlineData(SessionAccessLevel.Viewer, true, false, false)]
    [InlineData(SessionAccessLevel.None, false, false, false)]
    public void File_permissions_follow_the_session_role(
        SessionAccessLevel level,
        bool read,
        bool write,
        bool manage)
    {
        Assert.Equal(read, SessionAccessRules.CanReadFiles(level));
        Assert.Equal(write, SessionAccessRules.CanWriteFiles(level));
        Assert.Equal(manage, SessionAccessRules.CanManageFiles(level));
    }

    [Fact]
    public async Task Viewer_user_lists_files_through_a_read_only_actor()
    {
        var files = new RecordingFileService();
        var controller = UserController(SessionAccessLevel.Viewer, files);

        var result = Assert.IsType<OkObjectResult>(await controller.List("s1", default));
        var response = Assert.IsAssignableFrom<IReadOnlyList<SessionFileResponse>>(result.Value);

        Assert.Single(response);
        Assert.False(files.LastActor!.CanWrite);
        Assert.False(files.LastActor.CanManage);
    }

    [Fact]
    public async Task Missing_user_access_is_hidden_as_not_found()
    {
        var controller = UserController(SessionAccessLevel.None, new RecordingFileService());

        Assert.IsType<NotFoundResult>(await controller.List("s1", default));
    }

    [Fact]
    public async Task Temporary_storage_failure_maps_to_a_stable_conflict_response()
    {
        var files = new RecordingFileService
        {
            ReserveError = new SessionFileException("temporary_storage_unavailable"),
        };
        var controller = UserController(SessionAccessLevel.Owner, files);

        var result = Assert.IsType<ConflictObjectResult>(await controller.Reserve(
            "s1", new ReserveSessionFileRequest("shot.png", "image/png", 8, null), default));
        var error = Assert.IsType<FileApiError>(result.Value);

        Assert.Equal("temporary_storage_unavailable", error.Code);
    }

    [Fact]
    public async Task Pdf_content_sets_nosniff_and_a_sandbox_policy()
    {
        var files = new RecordingFileService { ContentMimeType = "application/pdf" };
        var controller = UserController(SessionAccessLevel.Viewer, files);

        var result = Assert.IsType<FileStreamResult>(
            await controller.Content("s1", "f1", default));

        var disposition = controller.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("inline", disposition, StringComparison.Ordinal);
        Assert.Contains("report.pdf", disposition, StringComparison.Ordinal);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions);
        Assert.Equal("sandbox; default-src 'none'",
            controller.Response.Headers.ContentSecurityPolicy);
    }

    [Fact]
    public async Task Viewer_share_lists_ready_files_through_the_read_only_service_path()
    {
        var files = new RecordingFileService();
        var controller = SharedController(ShareRole.Viewer, files);

        var result = Assert.IsType<OkObjectResult>(await controller.List("token", default));
        var response = Assert.IsAssignableFrom<IReadOnlyList<SessionFileResponse>>(result.Value);

        Assert.Single(response);
        Assert.False(files.LastActor!.CanWrite);
        Assert.False(files.LastActor.CanManage);
    }

    [Fact]
    public void Shared_controller_exposes_only_read_actions()
    {
        var actionNames = typeof(SharedSessionFilesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(new[] { "Capabilities", "Content", "List", "Presentation" }, actionNames);
    }

    private static SessionFilesController UserController(
        SessionAccessLevel level,
        RecordingFileService files)
    {
        var controller = new SessionFilesController(
            new FixedAccess(level), files, new NullArtifactStore(), new SessionFileOptions());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("preferred_username", "alice")], "test")),
            },
        };
        return controller;
    }

    private static SharedSessionFilesController SharedController(
        ShareRole role,
        RecordingFileService files) => new(
            new FixedAccess(SessionAccessRules.Resolve(false, role)),
            files,
            new NullArtifactStore(),
            new SessionFileOptions());

    private sealed class FixedAccess(SessionAccessLevel level) : ISessionAccessService
    {
        public Task<SessionAccessResult?> ResolveUserAsync(
            string principal, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result());
        public Task<SessionAccessResult?> ResolveTokenAsync(
            string token, CancellationToken ct = default) => Task.FromResult(Result());
        public Task<SessionAccessResult?> ResolveTokenReadOnlyAsync(
            string token, CancellationToken ct = default) => Task.FromResult(Result());

        private SessionAccessResult? Result() => level == SessionAccessLevel.None
            ? null
            : new SessionAccessResult(new SessionRecord
            {
                Id = "s1",
                Owner = "alice",
                CallbackToken = "secret",
            }, level, level == SessionAccessLevel.Owner ? null : "alice");
    }

    private sealed class RecordingFileService : ISessionFileService
    {
        public SessionFileActor? LastActor { get; private set; }
        public SessionFileException? ReserveError { get; init; }
        public string ContentMimeType { get; init; } = "image/png";

        public Task<ReserveFileResult> ReserveAsync(SessionFileActor actor,
            ReserveSessionFileCommand request, CancellationToken ct = default)
        {
            LastActor = actor;
            if (ReserveError is not null) throw ReserveError;
            return Task.FromResult(new ReserveFileResult(File(),
                new FileUploadDescriptor("proxy", "/upload", new Dictionary<string, string>())));
        }
        public Task<IReadOnlyList<SessionFileRecord>> ListAsync(SessionFileActor actor, CancellationToken ct = default)
        {
            LastActor = actor;
            return Task.FromResult<IReadOnlyList<SessionFileRecord>>([File()]);
        }
        public Task<AgentHub.Api.Files.FileContentResult> OpenContentAsync(SessionFileActor actor, string fileId, CancellationToken ct = default)
        {
            LastActor = actor;
            var name = ContentMimeType == "application/pdf" ? "report.pdf" : "shot.png";
            return Task.FromResult(new AgentHub.Api.Files.FileContentResult(
                new MemoryStream([1, 2, 3]), null, ContentMimeType, name, 3));
        }
        public Task<SessionFilePresentation?> GetPresentationAsync(SessionFileActor actor, CancellationToken ct = default)
        {
            LastActor = actor;
            return Task.FromResult<SessionFilePresentation?>(null);
        }
        public Task PutPodContentAsync(SessionFileActor actor, string fileId, Stream content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFileRecord> CompleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation> SetPresentationAsync(SessionFileActor actor, string? fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        private static SessionFileRecord File() => new(
            "f1", "s1", "alice", "shot.png", ".png", "image/png", "image/png", 3,
            SessionFileStorageKind.Pod, "f1/shot.png", SessionFileState.Ready,
            SessionFilePreviewState.None, null, "alice", "user", DateTime.UtcNow,
            DateTime.UtcNow, null);
    }
}
