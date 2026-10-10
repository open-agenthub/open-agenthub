using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Mcp;
using AgentHub.Api.Services;
using AgentHub.Api.WebSockets;
using AgentHub.Api.Ee.Sharing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

// (De)serialize enums as strings – the frontend sends e.g. mode: "Interactive".
builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<ISessionService, KubernetesSessionService>();
builder.Services.AddSingleton<AgentHub.Api.Files.ISessionFileRegistry, AgentHub.Api.Files.PostgresSessionFileRegistry>();
builder.Services.AddSingleton<AgentHub.Api.Files.IAgentCallbackAuthorizer, AgentHub.Api.Files.AgentCallbackAuthorizer>();
builder.Services.AddSingleton<AgentHub.Api.Persistence.ISessionStore, AgentHub.Api.Persistence.PostgresSessionStore>();
builder.Services.AddSingleton<AgentHub.Api.Persistence.ISessionMessageStore, AgentHub.Api.Persistence.PostgresSessionMessageStore>();
builder.Services.AddSingleton<AgentHub.Api.Browser.IBrowserLeaseStore, AgentHub.Api.Browser.PostgresBrowserLeaseStore>();
builder.Services.AddSingleton<AgentHub.Api.Browser.IBrowserSessionLock, AgentHub.Api.Browser.PostgresBrowserSessionLock>();
builder.Services.AddSingleton<AgentHub.Api.Browser.IAgentPodIdentityResolver, AgentHub.Api.Browser.KubernetesAgentPodIdentityResolver>();
builder.Services.AddSingleton<AgentHub.Api.Browser.IBrowserRequestAuthorizer, AgentHub.Api.Browser.BrowserRequestAuthorizer>();
builder.Services.AddSingleton<AgentHub.Api.Browser.IBrowserClusterClient, AgentHub.Api.Browser.KubernetesBrowserClusterClient>();
builder.Services.AddHttpClient<AgentHub.Api.Browser.IBrowserRuntimeClient, AgentHub.Api.Browser.BrowserRuntimeClient>();
builder.Services.AddSingleton<AgentHub.Api.Browser.IBrowserService, AgentHub.Api.Browser.KubernetesBrowserService>();
builder.Services.AddHostedService<AgentHub.Api.Browser.BrowserReconcileService>();
// One instance serves both roles: the hosted service owns the LISTEN connection that the
// publisher's notifications come back through, so registering it twice would leave half the
// subscribers attached to a bus that never listens.
builder.Services.AddSingleton<AgentHub.Api.Events.PostgresSessionEventBus>();
builder.Services.AddSingleton<AgentHub.Api.Events.ISessionEventBus>(
    sp => sp.GetRequiredService<AgentHub.Api.Events.PostgresSessionEventBus>());
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<AgentHub.Api.Events.PostgresSessionEventBus>());
builder.Services.AddSingleton<AgentHub.Api.Persistence.IProjectStore, AgentHub.Api.Persistence.PostgresProjectStore>();
// Library: MCP catalog (raw/api + org) + skills (community: personal, enterprise: shareable).
builder.Services.AddSingleton<AgentHub.Api.Library.IMcpSecretProtector, AgentHub.Api.Library.McpSecretProtector>();
builder.Services.AddSingleton<AgentHub.Api.Library.IMcpServerStore, AgentHub.Api.Library.McpServerStore>();
builder.Services.AddSingleton<AgentHub.Api.Library.IMcpGatewayTokenService, AgentHub.Api.Library.McpGatewayTokenService>();
builder.Services.AddSingleton<AgentHub.Api.Library.IEphemeralApiMcpStore, AgentHub.Api.Library.EphemeralApiMcpStore>();
builder.Services.AddSingleton(sp =>
    new AgentHub.Api.Library.ApiMcpGateway.OpenApiSpecCache(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("mcp-gateway")));
builder.Services.AddSingleton(sp =>
    new AgentHub.Api.Library.ApiMcpGateway.ApiMcpGatewayHandler(
        sp.GetRequiredService<AgentHub.Api.Library.IMcpServerStore>(),
        sp.GetRequiredService<AgentHub.Api.Library.IEphemeralApiMcpStore>(),
        sp.GetRequiredService<AgentHub.Api.Library.IMcpGatewayTokenService>(),
        sp.GetRequiredService<AgentHub.Api.Library.ApiMcpGateway.OpenApiSpecCache>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("mcp-gateway"),
        sp.GetRequiredService<AgentHub.Api.Persistence.ISessionStore>(),
        sp.GetRequiredService<AgentHub.Api.Library.ILibraryAccess>()));
// Do not follow redirects: a 3xx Location to loopback/metadata would bypass
// ValidateSafeOutboundUrl on the original URL (SSRF). Treat redirects as errors.
builder.Services.AddHttpClient("mcp-gateway")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddSingleton<AgentHub.Api.Library.ISkillStore, AgentHub.Api.Library.SkillStore>();
// EE share matrix: registered as the core ILibraryShareReader so access resolution
// consults real shares when the license is enabled (EmptyLibraryShareReader unused).
builder.Services.AddSingleton<AgentHub.Api.Ee.Library.LibraryShareStore>();
builder.Services.AddSingleton<AgentHub.Api.Ee.Library.ILibraryShareStore>(sp =>
    sp.GetRequiredService<AgentHub.Api.Ee.Library.LibraryShareStore>());
builder.Services.AddSingleton<AgentHub.Api.Library.ILibraryShareReader>(sp =>
    sp.GetRequiredService<AgentHub.Api.Ee.Library.LibraryShareStore>());
builder.Services.AddSingleton<AgentHub.Api.Library.ILibraryAccess, AgentHub.Api.Library.LibraryAccessService>();
// Skill search (FTS always; vector similarity when an embedding provider is configured)
// and the per-session skill-library MCP server.
builder.Services.AddSingleton<AgentHub.Api.Library.ISkillEmbeddingStore, AgentHub.Api.Library.SkillEmbeddingStore>();
builder.Services.AddSingleton<AgentHub.Api.Library.IEmbeddingProvider, AgentHub.Api.Library.OpenAiCompatibleEmbeddingProvider>();
builder.Services.AddSingleton<AgentHub.Api.Library.SkillSearchService>();
builder.Services.AddSingleton<AgentHub.Api.Library.SkillLibraryMcpService>();
builder.Services.AddSingleton<AgentHub.Api.Library.SkillImporter>();
builder.Services.AddSingleton<SessionShareStore>();
builder.Services.AddSingleton<ISessionAccessStore>(sp => sp.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionMcpPolicyReader>(sp => sp.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
// Every sharing surface (web, token API, MCP) goes through this one service, which holds the
// licence check — see docs/session-sharing-api.md. FrontendOrigin is what share-link URLs are
// built from, for the same reason SessionInfo.Url is (never the request host).
builder.Services.AddSingleton<ISessionShareStore>(sp => sp.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionSharingService>(sp => new SessionSharingService(
    sp.GetRequiredService<ISessionShareStore>(),
    sp.GetRequiredService<AgentHub.Api.Licensing.IEnterpriseLicense>(),
    builder.Configuration["FrontendOrigin"]));
builder.Services.AddSingleton<AgentHub.Api.Persistence.ApiTokenStore>();
// Token/cost usage aggregates fed by the agent pods' OpenTelemetry exporter.
builder.Services.AddSingleton<AgentHub.Api.Persistence.IUsageStore, AgentHub.Api.Persistence.PostgresUsageStore>();
// Monthly API budgets: personal limit (community) + admin limits (enterprise provider below).
builder.Services.AddSingleton<AgentHub.Api.Usage.IPersonalUsageLimitSource>(sp =>
    sp.GetRequiredService<AgentHub.Api.Persistence.UserDirectory>());
builder.Services.AddSingleton<AgentHub.Api.Usage.UsageLimitService>();
// S3 is optional: without an access key the platform runs without state/artifact persistence (no resume).
if (!string.IsNullOrWhiteSpace(builder.Configuration["S3:AccessKey"]))
    builder.Services.AddSingleton<AgentHub.Api.Storage.IArtifactStore, AgentHub.Api.Storage.S3ArtifactStore>();
else
    builder.Services.AddSingleton<AgentHub.Api.Storage.IArtifactStore, AgentHub.Api.Storage.NullArtifactStore>();
var sessionFileOptions = builder.Configuration.GetSection("Files")
    .Get<AgentHub.Api.Files.SessionFileOptions>() ?? new AgentHub.Api.Files.SessionFileOptions();
builder.Services.AddSingleton(sessionFileOptions);
builder.Services.AddHttpClient<AgentHub.Api.Files.IAgentFileClient, AgentHub.Api.Files.AgentFileClient>();
builder.Services.AddHttpClient<IAgentCredentialPusher, AgentCredentialPusher>();
builder.Services.AddHttpClient<ISessionMessageDelivery, SessionMessageDelivery>();
builder.Services.AddSingleton<AgentHub.Api.Files.ISessionFileService, AgentHub.Api.Files.SessionFileService>();
builder.Services.AddSingleton<AgentHub.Api.Files.ISessionFileCleanup, AgentHub.Api.Files.SessionFileCleanup>();
builder.Services.AddHostedService<AgentHub.Api.Files.SessionFileSweepService>();
builder.Services.AddHttpClient<AgentHub.Api.Files.IOfficePreviewClient, AgentHub.Api.Files.OfficePreviewClient>(client =>
{
    client.BaseAddress = new Uri(sessionFileOptions.OfficePreview.BaseUrl, UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHostedService<AgentHub.Api.Files.SessionFilePreviewWorker>();
builder.Services.AddHttpClient<AgentHub.Api.Notifications.INotifier, AgentHub.Api.Notifications.N8nNotifier>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IGitAuthService, GitAuthService>();

// Git webhook triggers: inbound GitLab/GitHub MR/PR webhooks start autonomous sessions.
builder.Services.AddSingleton<AgentHub.Api.Webhooks.IWebhookSecretProtector, AgentHub.Api.Webhooks.WebhookSecretProtector>();
builder.Services.AddSingleton<AgentHub.Api.Persistence.IWebhookTriggerStore, AgentHub.Api.Persistence.PostgresWebhookTriggerStore>();
builder.Services.AddSingleton(new AgentHub.Api.Webhooks.WebhookDeduplicator());

// Enterprise license gate (offline-verified token, activated via the admin UI, stored in the DB).
// Register the concrete store once and alias the interface to it, so the seat reporter can
// use the extra check-in methods while everything else depends on ILicenseStore.
builder.Services.AddSingleton<AgentHub.Api.Licensing.LicenseStore>();
builder.Services.AddSingleton<AgentHub.Api.Licensing.ILicenseStore>(sp =>
    sp.GetRequiredService<AgentHub.Api.Licensing.LicenseStore>());
builder.Services.AddSingleton<AgentHub.Api.Licensing.IEnterpriseLicense, AgentHub.Api.Licensing.EnterpriseLicense>();
builder.Services.AddSingleton<AgentHub.Api.Admin.AdminAccess>();
// Enterprise: user groups from OAuth token claims, group→role mapping and admin usage limits.
// Registered unconditionally — every provider self-gates on the license at call time.
builder.Services.AddSingleton<AgentHub.Api.Ee.Identity.UserGroupStore>();
builder.Services.AddSingleton<AgentHub.Api.Ee.Usage.UsageLimitStore>();
builder.Services.AddSingleton<AgentHub.Api.Usage.IAdminUsageLimitProvider, AgentHub.Api.Ee.Usage.EeUsageLimitProvider>();
builder.Services.AddSingleton<AgentHub.Api.Admin.IAdminRoleProvider, AgentHub.Api.Ee.Usage.GroupRoleAdminProvider>();
// Allowed agent kinds: CE default allows all; EE provider replaces it and self-gates on the license
// (empty whitelist = unrestricted / all allowed).
builder.Services.AddSingleton<AgentHub.Api.Agents.IAllowedAgentsProvider, AgentHub.Api.Agents.AllowAllAgentsProvider>();
builder.Services.AddSingleton<AgentHub.Api.Ee.Agents.AllowedAgentsStore>();
builder.Services.AddSingleton<AgentHub.Api.Agents.IAllowedAgentsProvider, AgentHub.Api.Ee.Agents.EeAllowedAgentsProvider>();

// Monthly seat heartbeat: reports the licensed-user count and renews the license token.
builder.Services.AddHostedService<AgentHub.Api.Licensing.SeatUsageReporter>();
// License claims are cached per process; with several replicas an activation handled by
// one pod must propagate to the rest — this poller re-reads the store every ~30s.
builder.Services.AddHostedService<AgentHub.Api.Licensing.LicenseRefresher>();

// GDPR account deletion: full per-user data purge (DB, K8s secrets, S3).
builder.Services.AddSingleton<AgentHub.Api.Persistence.IAccountPurgeStore, AgentHub.Api.Persistence.AccountPurgeStore>();
builder.Services.AddSingleton<AgentHub.Api.Services.AccountDeletionService>();

// Community chat integrations (Telegram/Signal): session-to-conversation bindings.
builder.Services.AddSingleton<AgentHub.Api.Chat.ChatBindingStore>();
builder.Services.AddSingleton<AgentHub.Api.Chat.WorkingIndicator>();
// Git project listing/resolution for chat commands (/repos, /new +repo).
builder.Services.AddSingleton<AgentHub.Api.Chat.ChatRepoService>();

// Community: Telegram/Signal chat integrations (no license required).
var telegramOpts = builder.Configuration.GetSection("Chat:Telegram").Get<AgentHub.Api.Chat.Telegram.TelegramOptions>() ?? new();
builder.Services.AddSingleton(telegramOpts);
builder.Services.AddSingleton<AgentHub.Api.Chat.Telegram.TelegramClient>();
// The bot token is part of every Telegram request path, and the HTTP client factory's default
// loggers write the full request URI — drop them for this client so the token stays out of the logs.
builder.Services.AddHttpClient(AgentHub.Api.Chat.Telegram.TelegramOptions.HttpClientName).RemoveAllLoggers();
var signalOpts = builder.Configuration.GetSection("Chat:Signal").Get<AgentHub.Api.Chat.Signal.SignalOptions>() ?? new();
builder.Services.AddSingleton(signalOpts);
builder.Services.AddSingleton<AgentHub.Api.Chat.Signal.SignalClient>();
builder.Services.AddSingleton<AgentHub.Api.Chat.ChatLinkCodeStore>();
builder.Services.AddSingleton<AgentHub.Api.Chat.Telegram.TelegramConversationFactory>();
builder.Services.AddSingleton<AgentHub.Api.Notifications.INotifier, AgentHub.Api.Chat.Telegram.TelegramNotifier>();

// Enterprise: Slack integration (only active with a valid license + tokens).
var slackOpts = builder.Configuration.GetSection("Ee:Slack").Get<AgentHub.Api.Ee.Slack.SlackOptions>() ?? new();
builder.Services.AddSingleton(slackOpts);
builder.Services.AddSingleton<AgentHub.Api.Ee.Slack.SlackThreadStore>();
builder.Services.AddSingleton<AgentHub.Api.Ee.Slack.SlackClient>();
builder.Services.AddSingleton<AgentHub.Api.Persistence.UserDirectory>();
builder.Services.AddSingleton<AgentHub.Api.Permissions.PermissionStore>();
builder.Services.AddSingleton<AgentHub.Api.Ee.Slack.ISlackTargetResolver, AgentHub.Api.Ee.Slack.SlackTargetResolver>();
builder.Services.AddSingleton<AgentHub.Api.Notifications.INotifier, AgentHub.Api.Ee.Slack.SlackNotifier>();
// Slack permission prompts: one instance posts them (IPermissionNotifier) and later
// defuses expired ones (IPermissionPromptEditor) — register once, alias both interfaces.
builder.Services.AddSingleton<AgentHub.Api.Ee.Slack.SlackPermissionNotifier>();
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionNotifier>(sp =>
    sp.GetRequiredService<AgentHub.Api.Ee.Slack.SlackPermissionNotifier>());
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionPromptEditor>(sp =>
    sp.GetRequiredService<AgentHub.Api.Ee.Slack.SlackPermissionNotifier>());
builder.Services.AddHostedService<AgentHub.Api.Ee.Slack.SlackSocketModeService>();

// Community: Telegram permission prompts + long polling. Registered AFTER the Slack
// block on purpose — the permission relay tries notifiers in registration order
// (Slack first, then Telegram).
builder.Services.AddSingleton<AgentHub.Api.Chat.Telegram.TelegramPermissionNotifier>();
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionNotifier>(sp =>
    sp.GetRequiredService<AgentHub.Api.Chat.Telegram.TelegramPermissionNotifier>());
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionPromptEditor>(sp =>
    sp.GetRequiredService<AgentHub.Api.Chat.Telegram.TelegramPermissionNotifier>());
builder.Services.AddHostedService<AgentHub.Api.Chat.Telegram.TelegramUpdateService>();

// Community: Signal notifier + reaction-based permission prompts + receive loop.
// Registered AFTER Telegram — the permission relay chain is Slack → Telegram → Signal.
builder.Services.AddSingleton<AgentHub.Api.Chat.Signal.SignalPermissionNotifier>();
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionNotifier>(sp =>
    sp.GetRequiredService<AgentHub.Api.Chat.Signal.SignalPermissionNotifier>());
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionPromptEditor>(sp =>
    sp.GetRequiredService<AgentHub.Api.Chat.Signal.SignalPermissionNotifier>());
builder.Services.AddSingleton<AgentHub.Api.Notifications.INotifier, AgentHub.Api.Chat.Signal.SignalNotifier>();
builder.Services.AddHostedService<AgentHub.Api.Chat.Signal.SignalReceiveService>();

// Safety net: expires pending permission prompts whose hook never called /expire.
builder.Services.AddHostedService<AgentHub.Api.Permissions.PermissionSweepService>();

// Sessions with a self-deletion deadline (docs/session-expiry.md). The lock keeps two replicas
// from deleting the same session at once.
builder.Services.AddSingleton<ISessionExpiryLock, PostgresSessionExpiryLock>();
builder.Services.AddHostedService<SessionExpirySweepService>();

// Runtime network port requests: agents ask for extra ports (agenthub_network MCP),
// the owner approves via the permission channel, approved ports become NetworkPolicies.
builder.Services.AddSingleton<AgentHub.Api.Network.IPortGrantStore, AgentHub.Api.Network.PortGrantStore>();
builder.Services.AddSingleton<AgentHub.Api.Network.INetworkPolicyClient, AgentHub.Api.Network.KubernetesNetworkPolicyClient>();
builder.Services.AddSingleton<AgentHub.Api.Network.NetworkPortService>();
builder.Services.AddSingleton<AgentHub.Api.Network.INetworkSessionCleanup>(sp =>
    sp.GetRequiredService<AgentHub.Api.Network.NetworkPortService>());

builder.Services.AddHealthChecks();

// --- Auth: generic OIDC/JWT provider (e.g. Keycloak). Multi-user separation via preferred_username. ---
// Without an authority the backend runs in "auth disabled" mode (local development): every request = user "dev".
var oidc = builder.Configuration.GetSection("Oidc");
var authEnabled = !string.IsNullOrWhiteSpace(oidc["Authority"]);
AuthenticationBuilder authBuilder;
if (authEnabled)
{
    authBuilder = builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.Authority = oidc["Authority"];
            // Empty audience = skip audience validation. Providers like Keycloak
            // do not put the client id into "aud" unless an audience mapper is
            // configured on the client.
            if (string.IsNullOrWhiteSpace(oidc["Audience"]))
                o.TokenValidationParameters.ValidateAudience = false;
            else
                o.Audience = oidc["Audience"];
            o.RequireHttpsMetadata = oidc.GetValue("RequireHttpsMetadata", true);
            // WebSocket: the token may also arrive as a query parameter, since browser WebSockets cannot set headers.
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (string.IsNullOrEmpty(ctx.Token) &&
                        ctx.Request.Path.StartsWithSegments("/ws") &&
                        ctx.Request.Query.TryGetValue("access_token", out var t))
                        ctx.Token = t;
                    return Task.CompletedTask;
                }
            };
        });
}
else
{
    authBuilder = builder.Services
        .AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
}
builder.Services.AddAuthorization();

// --- Remote MCP endpoint (Streamable HTTP) and its OAuth 2.1 authorization server ---
// Opt-in: only active when Mcp:PublicUrl names the externally reachable HTTPS origin of this
// deployment. The value becomes the OAuth issuer and the audience of every token, so it is
// taken from configuration rather than from the request host.
var mcpOptions = new McpOAuthOptions
{
    PublicBaseUrl = builder.Configuration["Mcp:PublicUrl"] ?? "",
    AuthEnabled = authEnabled
};
builder.Services.AddSingleton(mcpOptions);

if (mcpOptions.IsConfigured)
{
    builder.Services.AddSingleton<McpClientStore>();
    builder.Services.AddHttpContextAccessor();

    // OpenIddict wants the certificates while services are still being registered, so these are
    // fetched synchronously here rather than in the post-build initialisation below.
    var mcpKeys = new McpSigningKeyStore(builder.Configuration);
    mcpKeys.InitializeAsync().GetAwaiter().GetResult();
    var mcpSigningCertificate = mcpKeys.GetSigningCertificateAsync().GetAwaiter().GetResult();
    var mcpEncryptionCertificate = mcpKeys.GetEncryptionCertificateAsync().GetAwaiter().GetResult();

    builder.Services.AddAntiforgery();
    builder.Services.AddMcpServer().WithHttpTransport()
        .WithTools<AgentHubMcpTools>()
        .WithTools<SessionSharingMcpTools>();

    if (authEnabled)
    {
        // Browser login used only by /connect/authorize. Neither scheme is the default, so the
        // API keeps authenticating with JwtBearer exactly as before.
        authBuilder
            .AddCookie(McpAuthorizationController.CookieScheme, o =>
            {
                o.Cookie.Name = "agenthub.mcp";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // The OIDC provider posts/redirects back cross-site, so Strict would drop the
                // correlation cookie and every login would fail with "correlation failed".
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(1);
            })
            .AddOpenIdConnect(McpAuthorizationController.OidcScheme, o =>
            {
                o.Authority = oidc["Authority"];
                o.ClientId = oidc["ClientId"] ?? "agenthub";
                // Empty for the usual public SPA client; PKCE carries the proof instead.
                o.ClientSecret = oidc["ClientSecret"];
                o.UsePkce = true;
                o.ResponseType = "code";
                o.SignInScheme = McpAuthorizationController.CookieScheme;
                o.RequireHttpsMetadata = oidc.GetValue("RequireHttpsMetadata", true);
                o.CallbackPath = "/connect/oidc-callback";
                o.SaveTokens = false;
                o.Scope.Clear();
                foreach (var scope in (oidc["Scope"] ?? "openid profile email")
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    o.Scope.Add(scope);
                // Match the claim the rest of the API keys ownership on.
                o.TokenValidationParameters.NameClaimType = "preferred_username";
            });
    }

    // The OpenIddict store is in-memory; only registered clients must survive a restart, and
    // those live in Postgres and are replayed below. A shared database root keeps the startup
    // seed and per-request scopes on the SAME store — otherwise the seeded "mcp" scope is
    // invisible at request time and OpenIddict rejects the resource with invalid_target.
    var oauthDbRoot = new InMemoryDatabaseRoot();
    builder.Services.AddDbContext<McpOAuthDbContext>(o =>
    {
        o.UseInMemoryDatabase("openiddict", oauthDbRoot);
        o.UseOpenIddict();
    });

    builder.Services.AddOpenIddict()
        .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<McpOAuthDbContext>())
        .AddServer(o =>
        {
            o.SetIssuer(new Uri(mcpOptions.Issuer));
            o.SetAuthorizationEndpointUris("connect/authorize")
             .SetTokenEndpointUris("connect/token");
            o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
            // PKCE is mandatory (OAuth 2.1 and the MCP authorization spec).
            o.RequireProofKeyForCodeExchange();
            o.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess, McpScopes.Mcp);
            // Not AddDevelopment*Certificate: those write the generated certificate into the
            // user's X509 store, i.e. to disk, and this container's root filesystem is read-only
            // — the write throws on every request that reaches an OAuth endpoint. These come from
            // Postgres instead, so they also outlive a restart and are shared across replicas.
            o.AddSigningCertificate(mcpSigningCertificate)
             .AddEncryptionCertificate(mcpEncryptionCertificate);
            // Self-contained signed (not encrypted) access tokens, so the resource server can
            // read them without a lookup and no token state needs persisting.
            o.DisableAccessTokenEncryption();
            o.DisableTokenStorage();
            o.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
            // Advertise the dynamic client registration endpoint so MCP clients find it.
            o.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(handler =>
                handler.UseInlineHandler(context =>
                {
                    context.Metadata["registration_endpoint"] = mcpOptions.Issuer + "/connect/register";
                    return default;
                }));
        })
        .AddValidation(o =>
        {
            // Validate against the local server instance. Pointing a JwtBearer handler at the
            // public URL would make the pod fetch discovery and JWKS from itself through the
            // ingress, which fails wherever the cluster cannot hairpin to its own hostname —
            // valid tokens would then be rejected.
            o.AddAudiences(mcpOptions.McpResource);
            o.UseLocalServer();
            o.UseAspNetCore();
        });

    // Clients target the resource via the RFC 8707 `resource` parameter, which OpenIddict
    // validates against this static list rather than against scope-to-resource mappings.
    builder.Services.Configure<OpenIddictServerOptions>(o =>
        o.Resources.Add(new Uri(mcpOptions.McpResource)));

    builder.Services.AddAuthorization(o => o.AddPolicy("McpPolicy", p =>
    {
        p.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        p.RequireAuthenticatedUser();
    }));
}

// CORS only for our own frontend (origin can be overridden via config).
var frontendOrigin = builder.Configuration["FrontendOrigin"] ?? "http://localhost:5173";
builder.Services.AddCors(c => c.AddDefaultPolicy(p =>
    p.WithOrigins(frontendOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// Create the Postgres schema idempotently.
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.ISessionStore>();
    await store.InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.ISessionMessageStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Files.ISessionFileRegistry>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Browser.IBrowserLeaseStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.IProjectStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Library.IMcpServerStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Library.IEphemeralApiMcpStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Library.ISkillStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Library.ISkillEmbeddingStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Ee.Library.LibraryShareStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<SessionShareStore>().InitializeAsync();
    var tokenStore = scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.ApiTokenStore>();
    await tokenStore.InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.IWebhookTriggerStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.IUsageStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Ee.Slack.SlackThreadStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Chat.ChatBindingStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Chat.ChatLinkCodeStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.UserDirectory>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Permissions.PermissionStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Network.IPortGrantStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Ee.Identity.UserGroupStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Ee.Usage.UsageLimitStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Ee.Agents.AllowedAgentsStore>().InitializeAsync();
    // License token lives in the DB — create its table, then load & verify it.
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Licensing.ILicenseStore>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Licensing.IEnterpriseLicense>().ReloadAsync();
}

// Prime the OAuth server: create the schema for registered MCP clients, associate the "mcp"
// scope with the resource, and replay the stored registrations into the in-memory store.
if (mcpOptions.IsConfigured)
{
    using var mcpScope = app.Services.CreateScope();
    var services = mcpScope.ServiceProvider;
    services.GetRequiredService<McpOAuthDbContext>().Database.EnsureCreated();

    var clientStore = services.GetRequiredService<McpClientStore>();
    await clientStore.InitializeAsync();

    // Associating the resource with the scope is what authorises a client that was granted the
    // scope to use the resource; without it OpenIddict refuses the request (ID2192).
    var scopeManager = services.GetRequiredService<IOpenIddictScopeManager>();
    if (await scopeManager.FindByNameAsync(McpScopes.Mcp) is null)
    {
        await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = McpScopes.Mcp,
            Resources = { mcpOptions.McpResource }
        });
    }

    // Without this replay every restart invalidates existing registrations, and clients fail
    // their next silent reconnect with invalid_client instead of simply asking to sign in.
    var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
    foreach (var stored in await clientStore.ListAsync())
    {
        if (await applications.FindByClientIdAsync(stored.ClientId) is null)
        {
            await applications.CreateAsync(
                McpClientDescriptor.Build(stored.ClientId, stored.DisplayName, stored.RedirectUris, mcpOptions));
        }
    }
}

// Behind a TLS-terminating ingress (Traefik/HAProxy) the backend sees plain http, so
// scheme-derived URLs — the checkout returnUrl same-origin check in particular — would
// reject the browser's https origin. Honor X-Forwarded-Proto ONLY: X-Forwarded-For must
// stay untrusted because pod identity (browser routes) is derived from the socket's
// RemoteIpAddress and must not be spoofable by an in-cluster caller.
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
    };
    // The ingress reaches us from a dynamic pod IP, not loopback; the default known-proxy
    // list would ignore the header entirely. Spoofing X-Forwarded-Proto only ever changes
    // the perceived scheme, which gates nothing security-relevant on its own.
    forwarded.KnownNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

app.UseCors();
app.UseWebSockets();

// RFC 9728: every 401 on /mcp must point the client at the protected-resource metadata, which
// is how an MCP client discovers where to authenticate.
if (mcpOptions.IsConfigured)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                {
                    // Replace rather than append: the validation handler already emitted a bare
                    // "Bearer", and a client that reads only the first header would never see the
                    // metadata pointer and could not discover where to authenticate.
                    context.Response.Headers["WWW-Authenticate"] =
                        $"Bearer resource_metadata=\"{mcpOptions.Issuer}/.well-known/oauth-protected-resource\"";
                }
                return Task.CompletedTask;
            });
        }
        await next();
    });
}

app.UseAuthentication();
app.UseAuthorization();

// Capture the signed-in identity (owner + email + name) per user — refreshed every few
// minutes rather than once per process, so group-claim changes in the IdP propagate
// without a backend restart. Background notifiers (Slack) resolve the email from here.
{
    var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
    var refreshEvery = TimeSpan.FromMinutes(5);
    var groupsClaim = builder.Configuration["Ee:Groups:Claim"] ?? AgentHub.Api.Ee.Identity.GroupClaims.DefaultClaim;
    app.Use(async (ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var owner = ctx.User.FindFirstValue("preferred_username") ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var now = DateTime.UtcNow;
            if (owner is not null && (!seen.TryGetValue(owner, out var last) || now - last >= refreshEvery))
            {
                seen[owner] = now; // benign race: a concurrent request only repeats the upsert
                var email = ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.User.FindFirstValue("email");
                var name = ctx.User.FindFirstValue("name") ?? ctx.User.FindFirstValue(ClaimTypes.Name);
                try
                {
                    await ctx.RequestServices.GetRequiredService<AgentHub.Api.Persistence.UserDirectory>()
                        .RecordLoginAsync(owner, email, name, ctx.RequestAborted);
                    // Enterprise: mirror the token's group memberships (no-op without license —
                    // groups drive role mapping and limits, both of which self-gate).
                    var license = ctx.RequestServices.GetRequiredService<AgentHub.Api.Licensing.IEnterpriseLicense>();
                    if (license.Enabled)
                    {
                        var groups = AgentHub.Api.Ee.Identity.GroupClaims.Extract(ctx.User, groupsClaim);
                        await ctx.RequestServices.GetRequiredService<AgentHub.Api.Ee.Identity.UserGroupStore>()
                            .ReplaceGroupsAsync(owner, groups, ctx.RequestAborted);
                    }
                }
                catch { seen.TryRemove(owner, out _); } // retry on the next request
            }
        }
        await next();
    });
}

// Remote MCP endpoint. Fail closed: without a configured public URL there is no authorization
// server, and an open /mcp would let a client connect without ever being challenged — it would
// look connected while every tool call failed. Answer explicitly instead.
if (mcpOptions.IsConfigured)
{
    app.MapMcp("/mcp").RequireAuthorization("McpPolicy");

    // RFC 9728: tells MCP clients which authorization server guards this resource.
    app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
    {
        resource = mcpOptions.McpResource,
        authorization_servers = new[] { mcpOptions.Issuer },
        bearer_methods_supported = new[] { "header" },
        scopes_supported = new[] { McpScopes.Mcp }
    })).AllowAnonymous();
}
else
{
    app.Map("/mcp", () => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "MCP endpoint disabled",
        detail: "Mcp:PublicUrl is not configured on this instance. Set it to the externally "
              + "reachable HTTPS origin of this deployment to enable the MCP server."))
        .AllowAnonymous();
}

app.MapControllers();
app.MapHealthChecks("/healthz").AllowAnonymous();

// Catalog OpenAPI → MCP HTTP gateway (session-bound token; not OIDC).
// Agents connect with type:http + X-AgentHub-Mcp-Token (see McpConfigAssembler).
app.MapPost("/mcp/api/{id}", async (HttpContext ctx, string id,
    AgentHub.Api.Library.ApiMcpGateway.ApiMcpGatewayHandler handler) =>
{
    await handler.HandleCatalogAsync(ctx, id);
}).AllowAnonymous();
app.MapMethods("/mcp/api/{id}", new[] { "GET" }, () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed))
    .AllowAnonymous();

// Session-scoped ephemeral OpenAPI/GraphQL → MCP HTTP gateway.
app.MapPost("/mcp/session/{sessionId}/{name}", async (HttpContext ctx, string sessionId, string name,
    AgentHub.Api.Library.ApiMcpGateway.ApiMcpGatewayHandler handler) =>
{
    await handler.HandleSessionAsync(ctx, sessionId, name);
}).AllowAnonymous();
app.MapMethods("/mcp/session/{sessionId}/{name}", new[] { "GET" },
    () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed)).AllowAnonymous();

// Runtime config for the frontend (static nginx image, no build-time env vars):
// empty authority = auth disabled, so the frontend does not enforce a login.
app.MapGet("/api/config", (IGitAuthService git, AgentHub.Api.Ee.Slack.SlackOptions slack) => Results.Ok(new
{
    authority = oidc["Authority"] ?? "",
    clientId = oidc["ClientId"] ?? "agenthub",
    scope = oidc["Scope"] ?? "openid profile email",
    // Lets the UI show the "Connect GitHub/GitLab" account section only when configured.
    gitEnabled = git.AnyConfigured,
    // Lets the UI show the per-user Slack settings section only when Slack is enabled.
    slackEnabled = slack.Enabled,
    // Same for the community Telegram/Signal integrations.
    telegramEnabled = telegramOpts.Enabled,
    signalEnabled = signalOpts.Enabled,
    // Shown in the sidebar footer and on the profile page so a deployment can be told apart
    // from the release notes it is supposed to match.
    version = AgentHub.Api.Services.AppVersion.Current,
    repoUrl = AgentHub.Api.Services.AppVersion.RepoUrl
})).AllowAnonymous();

var agentPort = builder.Configuration.GetValue("AgentHub:AgentPort", 7681);

// --- Terminal / shell streams: browser WS -> proxy -> agent pod ---
// The agent serves the Claude terminal on "/" and an interactive bash shell on
// "/shell" (same port). Both proxy routes share auth and the owner check.
static string? WsOwner(HttpContext ctx)
    => ctx.User.FindFirstValue("preferred_username") ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);

async Task ProxyWs(HttpContext ctx, string id, ISessionService sessions, ILoggerFactory lf, string upstreamPath)
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.StatusCode = 401; return; }
    var owner = WsOwner(ctx);
    if (owner is null) { ctx.Response.StatusCode = 401; return; }
    await TerminalProxy.HandleAsync(ctx, owner, id, sessions, lf, agentPort, upstreamPath);
}

app.Map("/ws/sessions/{id}/terminal", (HttpContext ctx, string id,
        ISessionAccessService access, ISessionService sessions, ILoggerFactory lf) => ProxySharedWs(ctx, id, access, sessions, lf))
    .RequireAuthorization();

// The access service knows who may attach (owner, invited user, link token) and at
// which level; the live session (pod IP, phase) still comes from the session service
// under the owner's identity.
async Task ProxyResolvedWs(HttpContext ctx, SessionAccessResult resolved, ISessionService sessions, ILoggerFactory lf)
{
    var live = await sessions.GetSessionAsync(resolved.Session.Owner, resolved.Session.Id, ctx.RequestAborted);
    if (live is null) { ctx.Response.StatusCode = 404; return; }
    if (resolved.Level == SessionAccessLevel.Owner)
        try { await sessions.ClearQuestionAsync(resolved.Session.Owner, resolved.Session.Id, ctx.RequestAborted); } catch { }
    // Anyone allowed to type — owner or collaborator — keeps the idle countdown from running
    // out; the proxy ignores the callback for a read-only viewer.
    await TerminalProxy.HandleAsync(ctx, live, SessionAccessRules.CanWriteTerminal(resolved.Level), lf, agentPort,
        onActivity: ct => sessions.TouchActivityAsync(resolved.Session.Owner, resolved.Session.Id, ct));
}

async Task ProxySharedWs(HttpContext ctx, string id, ISessionAccessService access, ISessionService sessions, ILoggerFactory lf)
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var principal = WsOwner(ctx);
    if (principal is null) { ctx.Response.StatusCode = 401; return; }
    var resolved = await access.ResolveUserAsync(principal, id, ctx.RequestAborted);
    if (resolved is null) { ctx.Response.StatusCode = 404; return; }
    await ProxyResolvedWs(ctx, resolved, sessions, lf);
}

async Task ProxyLinkWs(HttpContext ctx, string token, ISessionAccessService access, ISessionService sessions, ILoggerFactory lf)
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var resolved = await access.ResolveTokenAsync(token, ctx.RequestAborted);
    if (resolved is null) { ctx.Response.StatusCode = 404; return; }
    await ProxyResolvedWs(ctx, resolved, sessions, lf);
}

app.Map("/ws/shared/{token}/terminal", (HttpContext ctx, string token,
    ISessionAccessService access, ISessionService sessions, ILoggerFactory lf) => ProxyLinkWs(ctx, token, access, sessions, lf));
async Task ProxyBrowserWs(HttpContext ctx, SessionAccessResult resolved,
    AgentHub.Api.Browser.IBrowserService browsers, ILoggerFactory lf,
    Func<CancellationToken, Task<bool>> remainsAuthorized)
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await BrowserProxy.HandleAsync(ctx, resolved.Session.Id,
        SessionAccessRules.CanWriteTerminal(resolved.Level), browsers, lf, remainsAuthorized);
}

async Task ProxyUserBrowserWs(HttpContext ctx, string id, ISessionAccessService access,
    AgentHub.Api.Browser.IBrowserService browsers, ILoggerFactory lf)
{
    var principal = WsOwner(ctx);
    if (principal is null) { ctx.Response.StatusCode = 401; return; }
    var resolved = await access.ResolveUserAsync(principal, id, ctx.RequestAborted);
    if (resolved is null) { ctx.Response.StatusCode = 404; return; }
    var initialWrite = SessionAccessRules.CanWriteTerminal(resolved.Level);
    await ProxyBrowserWs(ctx, resolved, browsers, lf, async ct =>
    {
        var current = await access.ResolveUserAsync(principal, id, ct);
        return current?.Session.Id == resolved.Session.Id &&
            SessionAccessRules.CanWriteTerminal(current.Level) == initialWrite;
    });
}

async Task ProxyLinkBrowserWs(HttpContext ctx, string token, ISessionAccessService access,
    AgentHub.Api.Browser.IBrowserService browsers, ILoggerFactory lf)
{
    var resolved = await access.ResolveTokenAsync(token, ctx.RequestAborted);
    if (resolved is null) { ctx.Response.StatusCode = 404; return; }
    var initialWrite = SessionAccessRules.CanWriteTerminal(resolved.Level);
    await ProxyBrowserWs(ctx, resolved, browsers, lf, async ct =>
    {
        var current = await access.ResolveTokenReadOnlyAsync(token, ct);
        return current?.Session.Id == resolved.Session.Id &&
            SessionAccessRules.CanWriteTerminal(current.Level) == initialWrite;
    });
}
app.Map("/ws/sessions/{id}/browser", (HttpContext ctx, string id,
        ISessionAccessService access, AgentHub.Api.Browser.IBrowserService browsers, ILoggerFactory lf) =>
        ProxyUserBrowserWs(ctx, id, access, browsers, lf))
    .RequireAuthorization();
app.Map("/ws/shared/{token}/browser", (HttpContext ctx, string token,
    ISessionAccessService access, AgentHub.Api.Browser.IBrowserService browsers, ILoggerFactory lf) =>
    ProxyLinkBrowserWs(ctx, token, access, browsers, lf));

app.Map("/ws/sessions/{id}/shell", (HttpContext ctx, string id,
        ISessionService sessions, ILoggerFactory lf) => ProxyWs(ctx, id, sessions, lf, "/shell"))
    .RequireAuthorization();

// --- Session events: server push replacing the workspace's poll loops ---
// Unlike the terminal and browser routes this proxies nothing to the pod; the backend is the
// source, so access is resolved here and re-checked for as long as the socket is held.
async Task SessionEventsWs(HttpContext ctx, string id, ISessionAccessService access,
    AgentHub.Api.Events.ISessionEventBus eventBus, ILoggerFactory lf)
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var principal = WsOwner(ctx);
    if (principal is null) { ctx.Response.StatusCode = 401; return; }
    var resolved = await access.ResolveUserAsync(principal, id, ctx.RequestAborted);
    if (resolved is null) { ctx.Response.StatusCode = 404; return; }
    await SessionEventSocket.HandleAsync(ctx, resolved.Session.Id, eventBus, lf,
        async ct => (await access.ResolveUserAsync(principal, id, ct))?.Session.Id == resolved.Session.Id);
}

async Task SharedSessionEventsWs(HttpContext ctx, string token, ISessionAccessService access,
    AgentHub.Api.Events.ISessionEventBus eventBus, ILoggerFactory lf)
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var resolved = await access.ResolveTokenAsync(token, ctx.RequestAborted);
    if (resolved is null) { ctx.Response.StatusCode = 404; return; }
    await SessionEventSocket.HandleAsync(ctx, resolved.Session.Id, eventBus, lf,
        async ct => (await access.ResolveTokenReadOnlyAsync(token, ct))?.Session.Id == resolved.Session.Id);
}

app.Map("/ws/sessions/{id}/events", (HttpContext ctx, string id, ISessionAccessService access,
        AgentHub.Api.Events.ISessionEventBus eventBus, ILoggerFactory lf) =>
        SessionEventsWs(ctx, id, access, eventBus, lf))
    .RequireAuthorization();
app.Map("/ws/shared/{token}/events", (HttpContext ctx, string token, ISessionAccessService access,
    AgentHub.Api.Events.ISessionEventBus eventBus, ILoggerFactory lf) =>
    SharedSessionEventsWs(ctx, token, access, eventBus, lf));

app.Run();

// Dev auth (only active without a configured Oidc__Authority): authenticates every request as "dev",
// so [Authorize] endpoints and owner separation work locally without an OIDC provider.
file sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("preferred_username", "dev")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
