using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using k8s;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentHub.Api.Tests;

public class ProviderSwitchHttpTests
{
    [PostgreSqlFact]
    public async Task ExistingEmptyDirHasExplicitReasonAndOtherUsersCannotReviewOrStart()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new() { Id = "s", Owner = "alice", Agent = AgentKind.Claude,
            AgentSessionId = "native", CallbackToken = "source" });
        var service = new KubernetesSessionService(database.Configuration, database.Sessions, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, NullLogger<KubernetesSessionService>.Instance,
            cluster: new Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" }),
            runtimeUpdateLock: new PostgresSessionExpiryLock(database.Configuration));
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISessionService>(service);
        builder.Services.AddControllers().AddApplicationPart(typeof(ProviderSwitchController).Assembly);
        builder.Services.AddAuthentication("proof").AddScheme<AuthenticationSchemeOptions, OwnerAuth>("proof", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0");
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/sessions/s/provider-switch")).StatusCode);
            http.DefaultRequestHeaders.Add("X-Proof-Owner", "alice");
            var status = await http.GetFromJsonAsync<ProviderSwitchAvailability>("/api/sessions/s/provider-switch");
            Assert.False(status!.Available);
            Assert.Contains("emptyDir", status.Reason);
            Assert.Contains("Migration is not supported", status.Reason);
            var selection = new ProviderSwitchSelection(Guid.NewGuid(), new(AgentKind.Codex, AgentAuthMode.Subscription, null, null, null),
                new(AgentKind.Codex, "keep restrictions", "continue"));
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/sessions/s/provider-switch/preview", selection)).StatusCode);
            http.DefaultRequestHeaders.Remove("X-Proof-Owner"); http.DefaultRequestHeaders.Add("X-Proof-Owner", "bob");
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/sessions/s/provider-switch")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/sessions/s/provider-switch/preview", selection)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/sessions/s/provider-switch", selection)).StatusCode);
            Assert.False((await database.Sessions.GetAsync("alice", "s"))!.ProviderSwitchPending);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class OwnerAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var owner = Request.Headers["X-Proof-Owner"].ToString();
            if (owner is not ("alice" or "bob")) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "proof"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "proof")));
        }
    }
}
