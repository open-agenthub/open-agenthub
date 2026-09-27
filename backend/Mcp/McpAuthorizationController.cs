using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AgentHub.Api.Mcp;

/// <summary>
/// OAuth 2.1 authorization, token and dynamic-registration endpoints for the remote MCP
/// server. User authentication is federated: the authorize endpoint sends the browser through
/// the instance's own OIDC provider and, once signed in, issues an OpenIddict authorization
/// code whose access token identifies the user to <c>/mcp</c>.
///
/// On an instance without an OIDC authority the backend already treats every caller as the
/// single "dev" user (see DevAuthHandler), so there is nobody to federate to and the login
/// step is skipped — the same behaviour the rest of the API has in that mode.
/// </summary>
[AllowAnonymous]
public sealed class McpAuthorizationController(
    McpOAuthOptions options,
    McpClientStore clients,
    IAntiforgery antiforgery,
    ILogger<McpAuthorizationController> logger) : Controller
{
    /// <summary>Scheme names for the browser login used only by this controller.</summary>
    public const string CookieScheme = "McpCookie";
    public const string OidcScheme = "McpOidc";

    /// <summary>The user an instance without an OIDC authority attributes everything to.</summary>
    public const string AnonymousUser = "dev";

    /// <summary>
    /// Registration is anonymous — a client has to exist before anybody signs in — so it is
    /// rate limited to stop an unauthenticated caller filling the table. Generous for real use.
    /// </summary>
    private const int MaxRegistrationsPerWindow = 30;
    private static readonly TimeSpan RegistrationWindow = TimeSpan.FromMinutes(10);
    private static readonly Lock RegistrationLock = new();
    private static readonly Queue<DateTime> RecentRegistrations = new();

    /// <summary>
    /// RFC 7591 dynamic client registration. MCP clients register themselves here to obtain a
    /// client_id before starting the OAuth flow; registered clients are public and must use PKCE.
    ///
    /// Registering grants nothing on its own — the client only becomes usable once a user
    /// approves it on the consent screen in <see cref="Authorize"/>. Two guards apply here:
    /// redirect URIs must be https or loopback (RFC 8252), and the endpoint is rate limited.
    /// </summary>
    [HttpPost("~/connect/register")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Register(
        [FromServices] IOpenIddictApplicationManager applications, CancellationToken ct)
    {
        if (!TryAcceptRegistration())
        {
            logger.LogWarning("Rejected an MCP client registration: rate limit reached");
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "temporarily_unavailable",
                error_description = "Too many client registrations. Please retry later."
            });
        }

        JsonElement request;
        try
        {
            using var reader = new StreamReader(Request.Body);
            request = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync(ct));
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "invalid_client_metadata" });
        }

        var redirectUris = new List<string>();
        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("redirect_uris", out var uris)
            && uris.ValueKind == JsonValueKind.Array)
        {
            redirectUris.AddRange(uris.EnumerateArray()
                .Select(u => u.GetString())
                .Where(u => !string.IsNullOrWhiteSpace(u))!);
        }

        if (redirectUris.Count == 0)
        {
            return BadRequest(new
            {
                error = "invalid_redirect_uri",
                error_description = "At least one redirect_uri is required."
            });
        }

        if (redirectUris.Any(uri => !McpRedirectUri.IsAcceptable(uri)))
        {
            return BadRequest(new
            {
                error = "invalid_redirect_uri",
                error_description = "Redirect URIs must use https, or http on a loopback address (RFC 8252)."
            });
        }

        var clientName = request.ValueKind == JsonValueKind.Object
                         && request.TryGetProperty("client_name", out var name)
            ? name.GetString() ?? "MCP client"
            : "MCP client";

        var clientId = Guid.NewGuid().ToString("n");
        await applications.CreateAsync(McpClientDescriptor.Build(clientId, clientName, redirectUris, options), ct);
        await clients.AddAsync(clientId, clientName, redirectUris, ct);

        logger.LogInformation("Registered MCP client {ClientId} ({ClientName})", clientId, clientName);

        return Ok(new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["redirect_uris"] = redirectUris,
            ["client_name"] = clientName
        });
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var userName = await ResolveUserAsync();
        if (userName is null)
        {
            // Not signed in: run the instance's own OIDC login, then come back to this request.
            return Challenge(
                authenticationSchemes: OidcScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + Request.QueryString
                });
        }

        // ---------------------------------------------------------------
        //  Consent
        //
        //  Clients register anonymously, so being registered must not be enough to act on a
        //  user's behalf. Without this step anyone could register a client with their own
        //  redirect URI and obtain a signed-in user's MCP token from a single link click,
        //  because the browser already carries a valid session cookie.
        //
        //  The approval is stored per (client, user), so a legitimate client asks exactly once
        //  and reconnects silently afterwards.
        // ---------------------------------------------------------------
        if (!await clients.IsApprovedByAsync(request.ClientId, userName, ct))
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                // prompt=none must never show UI.
                return ForbidOAuth(Errors.ConsentRequired, "The user has not approved this application yet.");
            }

            var isFormPost = Request.HasFormContentType;
            if (isFormPost && Request.Form.ContainsKey("submit.Deny"))
            {
                return ForbidOAuth(Errors.AccessDenied, "The user denied the request.");
            }

            if (isFormPost && Request.Form.ContainsKey("submit.Accept"))
            {
                // Without this the consent screen itself would be forgeable: a cross-site form
                // post could approve an attacker's client silently.
                try
                {
                    await antiforgery.ValidateRequestAsync(HttpContext);
                }
                catch (AntiforgeryValidationException)
                {
                    logger.LogWarning("Rejected an MCP consent post with an invalid antiforgery token");
                    return ForbidOAuth(Errors.AccessDenied, "The consent request could not be verified.");
                }

                if (!await clients.ApproveAsync(request.ClientId!, userName, ct))
                {
                    return ForbidOAuth(Errors.InvalidClient, "The application is not registered.");
                }

                logger.LogInformation("User {User} approved MCP client {ClientId}", userName, request.ClientId);
            }
            else
            {
                return ConsentPage(request, userName);
            }
        }

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // The rest of the API resolves the owner from "preferred_username"; keep the MCP token
        // carrying the same value so sessions created here belong to the same user.
        identity.SetClaim(Claims.Subject, userName);
        identity.SetClaim(Claims.Name, userName);
        identity.SetClaim("preferred_username", userName);
        identity.SetScopes(request.GetScopes());

        // Bind the access token to the MCP resource (RFC 8707 audience).
        var resources = request.GetResources();
        identity.SetResources(resources.Any() ? resources.ToArray() : [options.McpResource]);
        identity.SetDestinations(GetDestinations);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return ForbidOAuth(Errors.UnsupportedGrantType, "The specified grant type is not supported.");
        }

        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result?.Principal is null)
        {
            return ForbidOAuth(Errors.InvalidGrant, "The token is no longer valid.");
        }

        var identity = new ClaimsIdentity(
            result.Principal.Claims,
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetDestinations(GetDestinations);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Resolves the signed-in user, or null when a browser login is still needed. Without an
    /// OIDC authority there is nothing to log in to, so the caller is the single local user.
    /// </summary>
    private async Task<string?> ResolveUserAsync()
    {
        if (!options.AuthEnabled) return AnonymousUser;

        var result = await HttpContext.AuthenticateAsync(CookieScheme);
        if (result?.Succeeded != true || result.Principal is null) return null;

        var name = result.Principal.FindFirstValue("preferred_username")
                   ?? result.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? result.Principal.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private IActionResult ForbidOAuth(string error, string description) => Forbid(
        authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        }));

    /// <summary>
    /// The consent screen. Rendered inline rather than through a view because this project
    /// serves an API plus a separate SPA and has no Razor pipeline.
    /// </summary>
    private IActionResult ConsentPage(OpenIddictRequest request, string userName)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        var clientName = WebUtility.HtmlEncode(request.ClientId ?? "An application");
        var user = WebUtility.HtmlEncode(userName);
        var field = WebUtility.HtmlEncode(tokens.FormFieldName);
        var token = WebUtility.HtmlEncode(tokens.RequestToken ?? "");
        // On a POST, OpenIddict reads the authorize parameters from the form body only — the
        // query string is ignored — so they have to be carried across as hidden fields. The
        // form posts to the bare path for that reason: it keeps the body the single source and
        // avoids the same parameter arriving twice.
        var action = WebUtility.HtmlEncode(Request.PathBase + Request.Path);
        var hidden = string.Join("\n      ", Request.Query.SelectMany(p => p.Value.Select(v =>
            $"""<input type="hidden" name="{WebUtility.HtmlEncode(p.Key)}" value="{WebUtility.HtmlEncode(v)}">""")));

        // $$ so the CSS braces stay literal; interpolations are {{ }}.
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Authorise MCP client</title>
            <style>
              :root { color-scheme: light dark; }
              body { font: 16px/1.5 system-ui, sans-serif; margin: 0; display: grid;
                     place-items: center; min-height: 100vh; }
              main { max-width: 32rem; padding: 2rem; }
              code { background: rgba(127,127,127,.18); padding: .1em .35em; border-radius: .25em; }
              ul { padding-left: 1.2rem; }
              .row { display: flex; gap: .75rem; margin-top: 1.5rem; }
              button { font: inherit; padding: .6rem 1.2rem; border-radius: .4rem;
                       border: 1px solid rgba(127,127,127,.5); cursor: pointer; }
              button[value="accept"] { background: #2563eb; color: #fff; border-color: #2563eb; }
            </style></head><body><main>
            <h1>Authorise this client?</h1>
            <p>The MCP client <code>{{clientName}}</code> is asking to act as <strong>{{user}}</strong>
               on this AgentHub instance.</p>
            <p>If you approve, it can:</p>
            <ul>
              <li>create, inspect and delete sessions you own</li>
              <li>send messages to your agents</li>
            </ul>
            <p>Approve only if you just started this connection yourself.</p>
            <form method="post" action="{{action}}">
              {{hidden}}
              <input type="hidden" name="{{field}}" value="{{token}}">
              <div class="row">
                <button type="submit" name="submit.Accept" value="accept">Approve</button>
                <button type="submit" name="submit.Deny" value="deny">Cancel</button>
              </div>
            </form>
            </main></body></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>Claims go into the access token; the identity token stays minimal.</summary>
    private static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
    {
        Claims.Name or Claims.Subject or "preferred_username" => [Destinations.AccessToken, Destinations.IdentityToken],
        _ => [Destinations.AccessToken]
    };

    private static bool TryAcceptRegistration()
    {
        lock (RegistrationLock)
        {
            var cutoff = DateTime.UtcNow - RegistrationWindow;
            while (RecentRegistrations.Count > 0 && RecentRegistrations.Peek() < cutoff) RecentRegistrations.Dequeue();
            if (RecentRegistrations.Count >= MaxRegistrationsPerWindow) return false;
            RecentRegistrations.Enqueue(DateTime.UtcNow);
            return true;
        }
    }
}
