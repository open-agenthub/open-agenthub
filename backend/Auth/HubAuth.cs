using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AgentHub.Api.Auth;

/// <summary>How the browser API authenticates its callers (docs/no-oidc-api-key.md).</summary>
public enum HubAuthMode
{
    /// <summary>JWTs from the configured OIDC provider; one owner per signed-in user.</summary>
    Oidc,
    /// <summary>One static key from a Secret; every caller holding it is the configured owner.</summary>
    ApiKey,
    /// <summary>No credential at all; every request is user "dev". Local development only.</summary>
    InsecureDev
}

/// <summary>
/// Picks the authentication mode from configuration, and refuses to pick one when nothing is
/// configured. Without OIDC the backend used to authenticate every request as an admin, so an
/// install that forgot to configure a login was open to anyone who could reach it; there is no
/// default any more that leaves the API open.
/// </summary>
public sealed class HubAuthOptions
{
    /// <summary>
    /// 32 characters: the chart generates 48 and a hand-picked key this short is still out of
    /// reach of guessing over the network. A shorter value is far more likely a placeholder
    /// ("changeme") than a key, and starting with it would look secured without being so.
    /// </summary>
    public const int MinApiKeyLength = 32;

    public const string DefaultApiKeyOwner = "admin";

    /// <summary>The owner every request is attributed to in insecure development mode.</summary>
    public const string InsecureDevUser = "dev";

    public HubAuthMode Mode { get; private init; }

    /// <summary>Owner the API key acts as; empty unless <see cref="Mode"/> is ApiKey.</summary>
    public string ApiKeyOwner { get; private init; } = "";

    // Only the hash is kept, so comparing a presented key never depends on the stored key's
    // length — FixedTimeEquals on the raw strings would return early on a length mismatch.
    private byte[] _apiKeyHash = [];

    /// <summary>The value <c>/api/config</c> reports so the frontend knows which login to show.</summary>
    public string ConfigName => Mode switch
    {
        HubAuthMode.Oidc => "oidc",
        HubAuthMode.ApiKey => "apiKey",
        _ => "insecureDev"
    };

    /// <summary>
    /// Resolves the mode, first match wins: OIDC authority, API key, explicit insecure opt-in.
    /// Throws <see cref="HubAuthConfigurationException"/> when none is configured or the key is
    /// unusable; the message is written for the person reading the pod log.
    /// </summary>
    public static HubAuthOptions Resolve(IConfiguration cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg["Oidc:Authority"]))
            return new HubAuthOptions { Mode = HubAuthMode.Oidc };

        var key = cfg["Auth:ApiKey"]?.Trim() ?? "";
        if (key.Length > 0)
        {
            if (key.Length < MinApiKeyLength)
                throw new HubAuthConfigurationException(
                    $"Auth:ApiKey is shorter than {MinApiKeyLength} characters. Use a random key, e.g. "
                    + "`openssl rand -base64 36`, or leave it to the chart, which generates one.");
            var owner = cfg["Auth:ApiKeyOwner"]?.Trim();
            return new HubAuthOptions
            {
                Mode = HubAuthMode.ApiKey,
                ApiKeyOwner = string.IsNullOrEmpty(owner) ? DefaultApiKeyOwner : owner,
                _apiKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key))
            };
        }

        // Parsed by hand: GetValue throws on an empty or misspelt value, and anything short of an
        // unambiguous "true" has to mean "not opted in" rather than a crash or an open API.
        if (bool.TryParse(cfg["Auth:InsecureDevMode"], out var insecure) && insecure)
            return new HubAuthOptions { Mode = HubAuthMode.InsecureDev };

        throw new HubAuthConfigurationException(
            "No authentication is configured, so the backend refuses to start. Configure one of: "
            + "Oidc:Authority (an OIDC provider), Auth:ApiKey (a static key of at least "
            + $"{MinApiKeyLength} characters; the chart generates one when oidc.authority is empty), or, "
            + "for local development only, Auth:InsecureDevMode=true, which lets anyone who can reach "
            + "the backend act as an admin.");
    }

    /// <summary>Constant-time check of a presented key. False outside ApiKey mode.</summary>
    public bool ApiKeyMatches(string? presented)
    {
        if (Mode != HubAuthMode.ApiKey || string.IsNullOrEmpty(presented)) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim()));
        return CryptographicOperations.FixedTimeEquals(hash, _apiKeyHash);
    }

    /// <summary>Test seam: options in ApiKey mode without going through configuration.</summary>
    public static HubAuthOptions ForApiKey(string key, string owner = DefaultApiKeyOwner) => new()
    {
        Mode = HubAuthMode.ApiKey,
        ApiKeyOwner = owner,
        _apiKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key))
    };
}

public sealed class HubAuthConfigurationException(string message) : Exception(message);

/// <summary>
/// Bearer authentication with the static API key. The key arrives exactly where an OIDC access
/// token does — the Authorization header, or <c>access_token</c> on <c>/ws</c> because browser
/// WebSockets cannot set headers — so the frontend and every WebSocket route need no second path.
/// </summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    HubAuthOptions hubAuth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = BearerToken(Request);
        // NoResult rather than Fail: anonymous endpoints (internal callbacks, the remote API with
        // personal tokens) receive their own bearer tokens here, and must stay unaffected.
        if (presented is null) return Task.FromResult(AuthenticateResult.NoResult());
        // The failure text reaches the logs; it must never carry the presented value.
        if (!hubAuth.ApiKeyMatches(presented))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

        var identity = new ClaimsIdentity(
            [new Claim("preferred_username", hubAuth.ApiKeyOwner), new Claim("name", hubAuth.ApiKeyOwner)],
            SchemeName, "preferred_username", ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"agenthub\"";
        return Task.CompletedTask;
    }

    public static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            return value.Length > 0 ? value : null;
        }
        if (request.Path.StartsWithSegments("/ws") &&
            request.Query.TryGetValue("access_token", out var q) && !string.IsNullOrEmpty(q))
            return q.ToString();
        return null;
    }
}

/// <summary>
/// Insecure development mode (Auth:InsecureDevMode): authenticates every request as "dev", so
/// [Authorize] endpoints and owner separation work locally without an OIDC provider or a key.
/// </summary>
public sealed class InsecureDevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("preferred_username", HubAuthOptions.InsecureDevUser)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
