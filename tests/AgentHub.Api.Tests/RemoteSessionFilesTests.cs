using System.Security.Claims;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class RemoteSessionFilesTests
{
    [Fact]
    public async Task Running_http_endpoint_requires_token_and_streams_file_content()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(RemoteSessionFilesController).Assembly)
            .AddControllersAsServices();
        var files = new RecordingFiles();
        builder.Services.AddTransient(_ => new RemoteSessionFilesController(
            (token, _) => Task.FromResult(token == "oah_valid" ? new RemoteCaller("alice", null) : null),
            new FixedAccess(), files));
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/remote/sessions/s1/files")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "oah_valid");
        var response = await client.GetAsync("/api/remote/sessions/s1/files/f1/content");
        response.EnsureSuccessStatusCode();
        Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await client.GetAsync("/api/remote/sessions/other/files/f1/content")).StatusCode);
        await app.StopAsync();
    }
    [Theory]
    [InlineData(null)]
    [InlineData("Bearer callback_secret")]
    [InlineData("Bearer oah_invalid")]
    public async Task Personal_token_is_required_before_accessing_files(string? header)
    {
        var files = new RecordingFiles();
        var controller = Controller(files, header);
        Assert.IsType<UnauthorizedResult>(await controller.List("s1", default));
        Assert.IsType<UnauthorizedResult>(await controller.Content("s1", "f1", default));
        Assert.Null(files.LastActor);
    }

    [Fact]
    public async Task Inaccessible_sessions_reveal_neither_listing_nor_content()
    {
        var files = new RecordingFiles();
        var controller = Controller(files);
        Assert.IsType<NotFoundResult>(await controller.List("other", default));
        Assert.IsType<NotFoundResult>(await controller.Content("other", "f1", default));
        Assert.Null(files.LastActor);
    }

    [Fact]
    public async Task Viewer_can_list_and_download_without_write_permission_or_storage_redirect()
    {
        var files = new RecordingFiles();
        var controller = Controller(files);
        var list = Assert.IsType<OkObjectResult>(await controller.List("s1", default));
        Assert.Single(Assert.IsType<SessionFileResponse[]>(list.Value));
        var result = Assert.IsType<FileStreamResult>(await controller.Content("s1", "f1", default));
        await using var stream = result.FileStream;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
        Assert.False(files.LastAllowRedirect);
        Assert.False(files.LastActor!.CanWrite);
        Assert.Equal("alice", files.LastActor.Principal);
        Assert.Equal("owner", files.LastActor.Owner);
    }

    [Fact]
    public async Task OAuth_tools_return_image_pixels_and_enforce_session_access()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"))
        };
        var files = new RecordingFiles();
        var tools = new SessionFileMcpTools(new HttpContextAccessor { HttpContext = context }, new FixedAccess(), files);
        Assert.Single(await tools.ListFiles("s1"));
        var result = await tools.ReadFile("s1", "f1");
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));
        Assert.Equal(new byte[] { 1, 2, 3 }, image.DecodedData.ToArray());
        Assert.False(files.LastAllowRedirect);
        Assert.Equal("session_not_found", (await Assert.ThrowsAsync<McpException>(() => tools.ReadFile("other", "f1"))).Message);
        Assert.Equal("file_not_found", (await Assert.ThrowsAsync<McpException>(() => tools.ReadFile("s1", "unknown"))).Message);
        files.Size = 21 * 1024 * 1024;
        Assert.Equal("file_too_large_use_stdio", (await Assert.ThrowsAsync<McpException>(() => tools.ReadFile("s1", "f1"))).Message);
    }

    private static RemoteSessionFilesController Controller(RecordingFiles files, string? header = "Bearer oah_valid")
    {
        var controller = new RemoteSessionFilesController(
            (token, _) => Task.FromResult(token == "oah_valid" ? new RemoteCaller("alice", null) : null),
            new FixedAccess(), files)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (header is not null) controller.Request.Headers.Authorization = header;
        return controller;
    }

    private sealed class FixedAccess : ISessionAccessService
    {
        public Task<SessionAccessResult?> ResolveUserAsync(string principal, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(principal == "alice" && sessionId == "s1"
                ? new SessionAccessResult(new SessionRecord { Id = "s1", Owner = "owner", CallbackToken = "unused" }, SessionAccessLevel.Viewer, "owner") : null);
        public Task<SessionAccessResult?> ResolveTokenAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingFiles : ISessionFileService
    {
        public SessionFileActor? LastActor;
        public bool LastAllowRedirect = true;
        public long Size = 3;
        public Task<IReadOnlyList<SessionFileRecord>> ListAsync(SessionFileActor actor, CancellationToken ct = default)
        {
            LastActor = actor;
            return Task.FromResult<IReadOnlyList<SessionFileRecord>>([new SessionFileRecord(
                "f1", "s1", "owner", "shot.png", ".png", "image/png", "image/png", Size,
                SessionFileStorageKind.Pod, "f1/shot.png", SessionFileState.Ready, SessionFilePreviewState.None,
                null, "owner", "agent", DateTime.UtcNow, DateTime.UtcNow, null)]);
        }
        public Task<AgentHub.Api.Files.FileContentResult> OpenContentAsync(SessionFileActor actor, string fileId, bool allowRedirect = true, CancellationToken ct = default)
        {
            LastActor = actor; LastAllowRedirect = allowRedirect;
            return Task.FromResult(new AgentHub.Api.Files.FileContentResult(new MemoryStream([1, 2, 3]), null, "image/png", "shot.png", 3));
        }
        public Task<ReserveFileResult> ReserveAsync(SessionFileActor actor, ReserveSessionFileCommand request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PutPodContentAsync(SessionFileActor actor, string fileId, Stream content, long? contentLength = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFileRecord> CompleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation?> GetPresentationAsync(SessionFileActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation> SetPresentationAsync(SessionFileActor actor, string? fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
