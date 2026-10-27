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

    /// <summary>
    /// The key is the defence against brute force: the rate limit is per client, and without an
    /// ingress a client can make itself a new one per request. Length alone lets "changeme"
    /// repeated four times through. A random hex key of 32 characters has about 14 distinct
    /// characters, and the chance that it has fewer than 8 is negligible. A repeated word, a
    /// date or a run of one letter has fewer.
    /// </summary>
    public const int MinDistinctApiKeyCharacters = 8;

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
                    + "`openssl rand -hex 24`, or leave it to the chart, which generates one.");
            if (key.Distinct().Count() < MinDistinctApiKeyCharacters)
                throw new HubAuthConfigurationException(
                    $"Auth:ApiKey uses fewer than {MinDistinctApiKeyCharacters} different characters, so it is "
                    + "not random. Use e.g. `openssl rand -hex 24`, or leave it to the chart, which generates one.");
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
    HubAuthOptions hubAuth, ApiKeyFailureLimiter limiter)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    private const string WrongKeyItem = "agenthub.apiKey.wrong";
    private const string LimitedItem = "agenthub.apiKey.limited";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = BearerToken(Request);
        // NoResult rather than Fail: anonymous endpoints (internal callbacks, the remote API with
        // personal tokens) receive their own bearer tokens here, and must stay unaffected.
        if (presented is null) return Task.FromResult(AuthenticateResult.NoResult());
        // Checked before comparing, and for the right key too: a limit that let a correct guess
        // through would not limit guessing at all.
        if (limiter.IsLimited(Context))
        {
            Context.Items[LimitedItem] = true;
            return Task.FromResult(AuthenticateResult.Fail("Too many failed API key attempts"));
        }
        // The failure text reaches the logs; it must never carry the presented value.
        if (!hubAuth.ApiKeyMatches(presented))
        {
            Context.Items[WrongKeyItem] = true;
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        var identity = new ClaimsIdentity(
            [new Claim("preferred_username", hubAuth.ApiKeyOwner), new Claim("name", hubAuth.ApiKeyOwner)],
            SchemeName, "preferred_username", ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    // A failure is counted here rather than in HandleAuthenticateAsync: only an endpoint that
    // needs this scheme challenges. Every other bearer token passes through authentication too —
    // personal tokens on /api/remote, OAuth tokens on /mcp — and counting those as wrong keys
    // would lock their owner out after a few legitimate calls.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(LimitedItem))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = ((int)ApiKeyFailureLimiter.Window.TotalSeconds).ToString();
            return Task.CompletedTask;
        }
        if (Context.Items.ContainsKey(WrongKeyItem))
        {
            limiter.RecordFailure(Context);
            Logger.LogWarning("Rejected a wrong API key from {Client} for {Method} {Path}",
                ApiKeyFailureLimiter.ClientOf(Context), Request.Method, ApiKeyFailureLimiter.LogSafe(Request.Path.Value));
        }
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

/// <summary>
/// Slows down guessing from one client: after <see cref="MaxFailuresPerClient"/> wrong keys
/// within <see cref="Window"/>, every key from that client — the right one too — gets 429 until
/// the window ends. The defence against brute force is the key's entropy, which
/// <see cref="HubAuthOptions.Resolve"/> enforces; this limit keeps one noisy client from filling
/// the log and from guessing at request rate.
///
/// The client is the connection's address plus the last X-Forwarded-For entry. Behind the
/// ingress every connection comes from the ingress pod, and the ingress appends the real client
/// address last, where a client cannot override it. Without an ingress a client can invent that
/// header and so get a fresh bucket per request. Accepted, because entropy carries the defence.
///
/// Deliberately no bucket for the connection address alone. Behind the ingress that is one
/// address for everybody, so any limit on it would let anyone lock out every user, the owner with
/// the right key included, by sending wrong keys. The header decides nothing else: pod identity
/// elsewhere keeps relying on the socket address only.
/// </summary>
public sealed class ApiKeyFailureLimiter(TimeProvider? time = null)
{
    public const int MaxFailuresPerClient = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxTrackedBuckets = 10_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _buckets = new();

    public static string AddressOf(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static string ClientOf(HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (last is null) return AddressOf(ctx);
        // Only an address counts. The value ends up in the log and as a bucket key, and anything
        // else in the header comes straight from the client.
        return System.Net.IPAddress.TryParse(last, out var ip)
            ? $"{AddressOf(ctx)} via {ip}"
            : $"{AddressOf(ctx)} via (not an address)";
    }

    /// <summary>
    /// Request-supplied text for a log line. Kestrel decodes the path, so an encoded CR/LF in the
    /// URL would otherwise start a forged log entry.
    /// </summary>
    public static string LogSafe(string? value)
        => (value ?? "").Replace("\r", "").Replace("\n", "");

    public bool IsLimited(HttpContext ctx) => Count(ClientOf(ctx)) >= MaxFailuresPerClient;

    public void RecordFailure(HttpContext ctx)
    {
        if (_buckets.Count > MaxTrackedBuckets) Prune();
        Add(ClientOf(ctx));
    }

    private int Count(string bucket)
        => _buckets.TryGetValue(bucket, out var b) && _time.GetUtcNow() - b.Start < Window ? b.Count : 0;

    private void Add(string bucket)
    {
        var now = _time.GetUtcNow();
        _buckets.AddOrUpdate(bucket, _ => (now, 1),
            (_, b) => now - b.Start < Window ? (b.Start, b.Count + 1) : (now, 1));
    }

    // Expired buckets go once the table is large, so invented X-Forwarded-For values from earlier
    // windows do not pile up for the life of the process.
    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, b) in _buckets)
            if (now - b.Start >= Window) _buckets.TryRemove(key, out _);
    }
}
