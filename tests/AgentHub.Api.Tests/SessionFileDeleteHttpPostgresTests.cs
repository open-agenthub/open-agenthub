using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Deleting a file and cancelling an upload over real HTTP: the shipped file controllers, file
/// service, access service and share store, on the real session, share and file tables. Only
/// object storage is in memory, so the test can see which objects are gone.
///
/// The roles come from real shares on purpose. A unit test with a fixed access level proves what
/// the controller does with a level; this proves which level a direct share, a link and no share
/// at all actually resolve to on the delete routes.
/// </summary>
[Collection(SessionFileDeleteHttpCollection.Name)]
public sealed class SessionFileDeleteHttpPostgresTests
{
    [PostgreSqlFact]
    public async Task Owner_and_collaborator_delete_completed_files_and_viewers_cannot()
    {
        await using var rig = await Rig.StartAsync();
        var first = await rig.UploadAsync("alice", "first.txt");
        var second = await rig.UploadAsync("bob", "second.txt");

        Assert.Equal(HttpStatusCode.Forbidden, await rig.DeleteAsync("carol", first));
        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync("dave", first));
        // The share link routes have no delete at all, for either role a link can carry: no
        // route matches, and the listing below shows nothing went.
        Assert.Equal(HttpStatusCode.NotFound, await rig.SendAsync(null, HttpMethod.Delete,
            $"/api/shared/{rig.ViewerLink}/files/{first}"));
        Assert.Equal(HttpStatusCode.NotFound, await rig.SendAsync(null, HttpMethod.Delete,
            $"/api/shared/{rig.CollaboratorLink}/files/{first}"));
        Assert.Equal(["first.txt", "second.txt"], await rig.ListAsync("carol"));
        Assert.Equal(2, rig.Artifacts.Objects.Count);

        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync("bob", first));
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync("alice", second));

        Assert.Empty(await rig.ListAsync("alice"));
        Assert.Empty(rig.Artifacts.Objects);
        Assert.Equal(SessionFileState.Deleted, (await rig.Registry.GetAsync("s1", first))!.State);
        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync("alice", first));
    }

    /// <summary>
    /// Without an Enterprise licence shares do not resolve, so the session owner is the only one
    /// left who can delete; a collaborator's share still in the table does not reach the route.
    /// </summary>
    [PostgreSqlFact]
    public async Task Without_a_licence_the_owner_deletes_and_a_shared_collaborator_finds_nothing()
    {
        await using var rig = await Rig.StartAsync(licensed: false);
        var file = await rig.UploadAsync("alice", "owner.txt");
        var other = await rig.UploadAsync("alice", "other.txt");

        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync("bob", file));
        Assert.Equal(HttpStatusCode.NotFound, await rig.DeleteAsync("carol", file));
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync("alice", file));

        Assert.Equal(["other.txt"], await rig.ListAsync("alice"));
        Assert.True(rig.Artifacts.Objects.ContainsKey(rig.Locator(other)));
        Assert.Single(rig.Artifacts.Objects);
    }

    [PostgreSqlFact]
    public async Task Delete_refuses_an_upload_in_flight_and_cancel_refuses_a_completed_file()
    {
        await using var rig = await Rig.StartAsync();
        var pending = await rig.ReserveAsync("alice", "pending.txt");
        var done = await rig.UploadAsync("alice", "done.txt");

        var refusedDelete = await rig.SendForErrorAsync("alice", HttpMethod.Delete, $"/api/sessions/s1/files/{pending}");
        var refusedCancel = await rig.SendForErrorAsync("alice", HttpMethod.Delete, $"/api/sessions/s1/files/{done}/upload");

        Assert.Equal((HttpStatusCode.Conflict, "file_upload_pending"), refusedDelete);
        Assert.Equal((HttpStatusCode.Conflict, "file_upload_complete"), refusedCancel);
        Assert.Equal(SessionFileState.Reserved, (await rig.Registry.GetAsync("s1", pending))!.State);
        Assert.Equal(SessionFileState.Ready, (await rig.Registry.GetAsync("s1", done))!.State);
        Assert.True(rig.Artifacts.Objects.ContainsKey(rig.Locator(done)));
    }

    [PostgreSqlFact]
    public async Task Only_the_uploader_or_the_owner_cancels_a_reservation_and_it_frees_the_quota()
    {
        await using var rig = await Rig.StartAsync(maxSessionFiles: 2);
        var alices = await rig.ReserveAsync("alice", "alice.txt");
        var bobs = await rig.ReserveAsync("bob", "bob.txt");
        Assert.Equal((HttpStatusCode.Conflict, "session_file_count_exceeded"),
            await rig.SendForErrorAsync("bob", HttpMethod.Post, "/api/sessions/s1/files/reserve",
                new { name = "third.txt", mimeType = "text/plain", size = 5 }));

        Assert.Equal(HttpStatusCode.Forbidden, await rig.CancelAsync("bob", alices));
        Assert.Equal(HttpStatusCode.Forbidden, await rig.CancelAsync("carol", bobs));
        Assert.Equal(HttpStatusCode.NotFound, await rig.CancelAsync("dave", bobs));
        Assert.Equal(HttpStatusCode.NotFound, await rig.SendAsync(null, HttpMethod.Delete,
            $"/api/shared/{rig.CollaboratorLink}/files/{bobs}/upload"));

        Assert.Equal(HttpStatusCode.NoContent, await rig.CancelAsync("bob", bobs));
        Assert.Equal(HttpStatusCode.NoContent, await rig.CancelAsync("alice", alices));

        Assert.Equal(SessionFileState.Deleted, (await rig.Registry.GetAsync("s1", alices))!.State);
        Assert.Equal(SessionFileState.Deleted, (await rig.Registry.GetAsync("s1", bobs))!.State);
        // The reservations no longer count, and a cancelled one cannot be completed afterwards.
        await rig.ReserveAsync("bob", "third.txt");
        Assert.Equal(HttpStatusCode.Conflict,
            await rig.SendAsync("bob", HttpMethod.Post, $"/api/sessions/s1/files/{bobs}/complete"));
    }

    [PostgreSqlFact]
    public async Task Deleting_a_presented_document_clears_the_presentation_and_takes_its_preview_along()
    {
        await using var rig = await Rig.StartAsync();
        var document = await rig.UploadAsync("alice", "notes.txt");
        var preview = await rig.UploadAsync("alice", "notes.preview.pdf.txt");
        // The worker's own link: claim moves the source to Converting, link points it at the PDF.
        await rig.Database.ExecuteAsync(
            $"UPDATE session_files SET preview_state = 'Queued' WHERE id = '{document}'");
        Assert.Equal(document, (await rig.Registry.ClaimPreviewAsync())!.Id);
        await rig.Registry.LinkPreviewAsync(document, preview, succeeded: true);
        Assert.Equal(HttpStatusCode.OK, await rig.SendAsync("bob", HttpMethod.Put,
            "/api/sessions/s1/files/presentation", new { fileId = document }));

        Assert.Equal((HttpStatusCode.Conflict, "file_is_preview"),
            await rig.SendForErrorAsync("bob", HttpMethod.Delete, $"/api/sessions/s1/files/{preview}"));
        Assert.Equal(HttpStatusCode.NoContent, await rig.DeleteAsync("bob", document));

        Assert.Empty(await rig.ListAsync("alice"));
        Assert.Empty(rig.Artifacts.Objects);
        Assert.Null((await rig.Registry.GetPresentationAsync("s1"))!.FileId);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _http;

        private Rig(PostgresSharingDatabase database, PostgresSessionFileRegistry registry,
            MemoryArtifacts artifacts, WebApplication app, string viewerLink, string collaboratorLink)
        {
            Database = database;
            Registry = registry;
            Artifacts = artifacts;
            _app = app;
            _http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            ViewerLink = viewerLink;
            CollaboratorLink = collaboratorLink;
        }

        public PostgresSharingDatabase Database { get; }
        public PostgresSessionFileRegistry Registry { get; }
        public MemoryArtifacts Artifacts { get; }
        public string ViewerLink { get; }
        public string CollaboratorLink { get; }

        public static async Task<Rig> StartAsync(int maxSessionFiles = 200, bool licensed = true)
        {
            var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
            foreach (var user in new[] { "alice", "bob", "carol", "dave" }) await database.AddUserAsync(user);
            await database.AddSessionAsync("alice", "s1");
            await database.Shares.UpsertDirectAsync("alice", "s1", "bob", ShareRole.Collaborator);
            await database.Shares.UpsertDirectAsync("alice", "s1", "carol", ShareRole.Viewer);
            var viewerLink = await database.Shares.CreateLinkAsync("alice", "s1", ShareRole.Viewer, null);
            var collaboratorLink = await database.Shares.CreateLinkAsync("alice", "s1", ShareRole.Collaborator, null);
            var registry = new PostgresSessionFileRegistry(database.Configuration);
            await registry.InitializeAsync();
            var artifacts = new MemoryArtifacts();
            var options = new SessionFileOptions { MaxSessionFiles = maxSessionFiles };

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication(HeaderUser.Scheme)
                .AddScheme<AuthenticationSchemeOptions, HeaderUser>(HeaderUser.Scheme, null);
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<ISessionAccessService>(
                new SessionAccessService(database.Shares, new FakeEnterpriseLicense(licensed)));
            builder.Services.AddSingleton<IArtifactStore>(artifacts);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<ISessionFileService>(new SessionFileService(
                registry, artifacts, DispatchProxy.Create<IAgentFileClient, Unused>(),
                DispatchProxy.Create<ISessionService, Unused>(), options));
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(SessionFilesController).Assembly)
                .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new OnlyFileControllers()));
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return new Rig(database, registry, artifacts, app, viewerLink.Token, collaboratorLink.Token);
        }

        public string Locator(string fileId) =>
            Artifacts.Objects.Keys.Single(key => key.Contains(fileId, StringComparison.Ordinal));

        public async Task<string> ReserveAsync(string user, string name)
        {
            using var response = await Send(user, HttpMethod.Post, "/api/sessions/s1/files/reserve",
                new { name, mimeType = "text/plain", size = 5 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("file").GetProperty("id").GetString()!;
        }

        public async Task<string> UploadAsync(string user, string name)
        {
            var id = await ReserveAsync(user, name);
            using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/sessions/s1/files/{id}/content"))
            {
                put.Headers.Add(HeaderUser.Header, user);
                put.Content = new ByteArrayContent("hello"u8.ToArray());
                using var stored = await _http.SendAsync(put);
                Assert.Equal(HttpStatusCode.NoContent, stored.StatusCode);
            }
            Assert.Equal(HttpStatusCode.OK,
                await SendAsync(user, HttpMethod.Post, $"/api/sessions/s1/files/{id}/complete"));
            return id;
        }

        public async Task<string[]> ListAsync(string user)
        {
            using var response = await Send(user, HttpMethod.Get, "/api/sessions/s1/files");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.EnumerateArray().Select(file => file.GetProperty("name").GetString()!).ToArray();
        }

        public Task<HttpStatusCode> DeleteAsync(string user, string fileId) =>
            SendAsync(user, HttpMethod.Delete, $"/api/sessions/s1/files/{fileId}");

        public Task<HttpStatusCode> CancelAsync(string user, string fileId) =>
            SendAsync(user, HttpMethod.Delete, $"/api/sessions/s1/files/{fileId}/upload");

        public async Task<HttpStatusCode> SendAsync(string? user, HttpMethod method, string path, object? body = null)
        {
            using var response = await Send(user, method, path, body);
            return response.StatusCode;
        }

        public async Task<(HttpStatusCode, string?)> SendForErrorAsync(
            string user, HttpMethod method, string path, object? body = null)
        {
            using var response = await Send(user, method, path, body);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, json.RootElement.GetProperty("code").GetString());
        }

        private Task<HttpResponseMessage> Send(string? user, HttpMethod method, string path, object? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            if (user is not null) request.Headers.Add(HeaderUser.Header, user);
            if (body is not null) request.Content = JsonContent.Create(body);
            return _http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    /// <summary>The signed-in user, named by a header: what the real OIDC handler yields as preferred_username.</summary>
    private sealed class HeaderUser(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "header-user";
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers[Header].ToString();
            if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("preferred_username", user)], Scheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }

    private sealed class OnlyFileControllers
        : Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo type) =>
            base.IsController(type) &&
            (type == typeof(SessionFilesController) || type == typeof(SharedSessionFilesController));
    }

    public class Unused : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException($"{method?.Name} is not on the delete path.");
    }

    internal sealed class MemoryArtifacts : IArtifactStore
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Bytes, string? Type)> Objects { get; } = new();
        public bool IsConfigured => true;
        public bool CanServeBrowsersDirectly => false;
        public async Task<bool> TryPutStreamAsync(string key, Stream content, string? contentType,
            long? contentLength = null, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Objects[key] = (buffer.ToArray(), contentType);
            return true;
        }
        public string PresignPut(string key, TimeSpan ttl) => throw new NotSupportedException();
        public string PresignGet(string key, TimeSpan ttl) => throw new NotSupportedException();
        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<ArtifactObjectInfo?> HeadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Objects.TryGetValue(key, out var stored)
                ? new ArtifactObjectInfo(stored.Bytes.Length, stored.Type)
                : null);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var stored) ? new MemoryStream(stored.Bytes) : null);
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SessionFileDeleteHttpCollection
{
    public const string Name = "session-file-delete-http";
}
