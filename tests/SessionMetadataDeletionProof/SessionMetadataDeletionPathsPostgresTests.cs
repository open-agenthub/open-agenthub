using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using AgentHub.Api.Agents;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Library;
using AgentHub.Api.Licensing;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using AgentHub.Api.Usage;
using k8s;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionMetadataDeletionPathsPostgresTests
{
    [PostgreSqlFact]
    public async Task EveryDeleteEntryPoint_RemovesMetadata_AndShareLinkReturnsAnonymous404()
    {
        await using var db = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await new SlackThreadStore(db.Configuration).InitializeAsync();
        await using var clusterHost = EmptyHost();
        var clusterRequests = 0;
        // Only the cluster boundary is replaced: the production service and SQL execute below.
        clusterHost.Run(context =>
        {
            Interlocked.Increment(ref clusterRequests);
            var path = context.Request.Path.Value!;
            if (context.Request.Method == "GET" &&
                new[] { "/pods", "/jobs", "/secrets" }.Any(path.EndsWith))
                return context.Response.WriteAsJsonAsync(new { apiVersion = "v1", kind = "List", items = Array.Empty<object>() });
            context.Response.StatusCode = 404;
            return context.Response.WriteAsJsonAsync(new { apiVersion = "v1", kind = "Status", code = 404, reason = "NotFound" });
        });
        await clusterHost.StartAsync();
        using var cluster = new Kubernetes(new KubernetesClientConfiguration { Host = clusterHost.Urls.Single() });
        var browsers = RuntimeProofProxy.Make<IBrowserService>((method, args) => method.Name switch
        {
            "GetSummariesAsync" => Task.FromResult<IReadOnlyDictionary<string, BrowserSummary>>(
                ((IEnumerable<string>)args![0]!).ToDictionary(id => id, _ => BrowserSummary.Stopped)),
            _ => null
        });
        var projects = RuntimeProofProxy.Make<IProjectStore>((method, _) => method.Name == "ListAsync"
            ? Task.FromResult<IReadOnlyList<ProjectInfo>>([]) : null);
        var locks = new PostgresSessionExpiryLock(db.Configuration);
        var library = RuntimeProofProxy.Make<ILibraryAccess>();
        var service = new KubernetesSessionService(db.Configuration, db.Sessions, projects,
            new NullArtifactStore(), browsers, RuntimeProofProxy.Make<IGitAuthService>(),
            new UsageLimitService(RuntimeProofProxy.Make<IUsageStore>(), RuntimeProofProxy.Make<IPersonalUsageLimitSource>(), []),
            new AllowAllAgentsProvider(), library, RuntimeProofProxy.Make<IMcpServerStore>(),
            new InMemoryEphemeralApiMcpStore(), RuntimeProofProxy.Make<IMcpGatewayTokenService>(),
            NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: locks, cluster: cluster);
        var tokens = new ApiTokenStore(db.Configuration);
        await tokens.InitializeAsync();
        await tokens.CreateAsync("owner", "proof", "oah_proof_delete_token");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(SessionsController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ISessionService>(service);
        builder.Services.AddSingleton<ISessionStore>(db.Sessions);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(new PermissionStore(db.Configuration));
        builder.Services.AddSingleton<ISessionMcpPolicyReader>(db.Shares);
        builder.Services.AddSingleton(library);
        builder.Services.AddSingleton<ISessionAccessService>(new SessionAccessService(db.Shares, new EnabledLicense()));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            // The harness supplies the authenticated identity; controllers still enforce ownership.
            if (context.Request.Headers.ContainsKey("X-Proof-Owner")) context.User = Owner();
            return next(context);
        });
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        await db.AddSessionAsync("owner", "parent");
        await db.AddSessionAsync("other", "control");
        await SessionMetadataDeletionPostgresTests.SeedAsync(db, "control");

        foreach (var path in new[] { "api", "remote", "child", "mcp", "expiry", "account", "purge" })
        {
            var record = new SessionRecord { Id = path, Owner = "owner", Title = "private title",
                CallbackToken = "callback-" + path, Mode = SessionMode.Interactive, ParentSessionId = "parent",
                CreatedAt = DateTime.UtcNow.AddDays(-2), LastActivityAt = DateTime.UtcNow.AddDays(-1),
                AutoDeleteAfterSeconds = path == "expiry" ? 60 : null };
            await db.Sessions.UpsertAsync(record);
            await SessionMetadataDeletionPostgresTests.SeedAsync(db, path);
            var link = await db.Shares.CreateLinkAsync("owner", path, ShareRole.Viewer, null);
            var linkUrl = $"/api/shared/{link.Token}/session";
            using (var before = await http.GetAsync(linkUrl)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            var requestsBefore = clusterRequests;
            switch (path)
            {
                case "api":
                case "remote":
                case "child":
                    var url = path == "api" ? $"/api/sessions/{path}" : path == "remote"
                        ? $"/api/remote/sessions/{path}" : $"/internal/sessions/parent/peer/{path}";
                    using (var request = new HttpRequestMessage(HttpMethod.Delete, url))
                    {
                        request.Headers.Add("X-Proof-Owner", "owner");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "oah_proof_delete_token");
                        request.Headers.Add("X-Agent-Token", "callback-parent");
                        using var response = await http.SendAsync(request);
                        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
                            $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                    }
                    break;
                case "mcp":
                    var tools = new AgentHubMcpTools(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Owner() } },
                        service, NullLogger<AgentHubMcpTools>.Instance);
                    Assert.Equal(path, await tools.DeleteSession(path));
                    break;
                case "expiry":
                    var sweep = new SessionExpirySweepService(db.Sessions, service, locks, [], NullLogger<SessionExpirySweepService>.Instance);
                    Assert.Equal(1, await sweep.SweepOnceAsync(DateTime.UtcNow, default));
                    break;
                case "account":
                    await new AccountDeletionService(service, new AccountPurgeStore(db.Configuration, NullLogger<AccountPurgeStore>.Instance),
                        new NullArtifactStore(), NullLogger<AccountDeletionService>.Instance).DeleteAccountAsync("owner");
                    break;
                case "purge":
                    await new AccountPurgeStore(db.Configuration, NullLogger<AccountPurgeStore>.Instance)
                        .PurgeAsync("owner", [path]);
                    break;
            }
            if (path != "purge") Assert.True(clusterRequests > requestsBefore);
            await SessionMetadataDeletionPostgresTests.AssertEmptyAsync(db, path);
            using var after = await http.GetAsync(linkUrl);
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
            var body = await after.Content.ReadAsStringAsync();
            Assert.DoesNotContain("detail", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private title", body);
            Assert.DoesNotContain("owner", body);
            Assert.DoesNotContain(path, body);
            Assert.Null(await db.Shares.FindTokenAccessAsync(link.Token));
            foreach (var table in SessionMetadataDeletionPostgresTests.Tables)
                Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE session_id = 'control'"));
        }
    }

    private static WebApplication EmptyHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder.Build();
    }

    private static ClaimsPrincipal Owner() => new(new ClaimsIdentity([new Claim("preferred_username", "owner")], "proof"));

    private sealed class EnabledLicense : IEnterpriseLicense
    {
        public LicenseStatus Status { get; } = new() { Valid = true };
        public bool Enabled => true;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
