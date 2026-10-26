using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// Git OAuth: lets a user connect their GitHub/GitLab account (incl. self-hosted
/// instances) so sessions can list, clone and push repositories without a manual
/// PAT. Tokens are stored in a per-user Kubernetes secret (gitauth-&lt;owner&gt;),
/// never in the database and never returned to the browser.
/// </summary>
public interface IGitAuthService
{
    bool AnyConfigured { get; }
    Task<IReadOnlyList<GitProviderInfo>> ListProvidersAsync(string owner, CancellationToken ct = default);
    string CreateAuthorizeUrl(string providerId, string owner, string redirectUri);
    Task<string?> HandleCallbackAsync(string providerId, string code, string state, string redirectUri, CancellationToken ct = default);
    Task DisconnectAsync(string owner, string providerId, CancellationToken ct = default);
    Task<IReadOnlyList<GitProject>> SearchProjectsAsync(string owner, string providerId, string? query, CancellationToken ct = default);
    /// <summary>Whether this instance has a provider of that id configured at all.</summary>
    bool IsConfigured(string providerId);
    /// <summary>Whether the owner has completed the OAuth flow for that provider and the provider
    /// has not refused the grant since.</summary>
    Task<bool> IsConnectedAsync(string owner, string providerId, CancellationToken ct = default);
    /// <summary>git-credentials file content (one line per provider host) for the repos'
    /// providers, or null if none of them use a connected OAuth provider.</summary>
    Task<string?> BuildCredentialStoreAsync(string owner, IEnumerable<RepoRef> repos, CancellationToken ct = default);

    /// <summary>
    /// Renews the owner's token for the provider when it is close to expiry (docs/git-credentials.md,
    /// "Renewing a connected provider's token"). One caller per owner and provider at a time, across
    /// replicas; with <paramref name="waitForLock"/> false a held lock answers
    /// <see cref="GitRefreshOutcome.Busy"/> at once.
    /// </summary>
    Task<GitRefreshOutcome> RefreshIfDueAsync(string owner, string providerId, bool waitForLock, CancellationToken ct = default)
        => Task.FromResult(GitRefreshOutcome.NotDue);
}

public enum GitRefreshOutcome
{
    /// <summary>Nothing to do: not connected, no expiry or refresh token, or not close to expiry.</summary>
    NotDue,
    Refreshed,
    /// <summary>The provider refused the refresh token; the owner has to sign in again.</summary>
    Rejected,
    /// <summary>The provider could not be reached or answered an error; the next pass retries.</summary>
    Failed,
    /// <summary>Another caller holds the lock for this owner and provider.</summary>
    Busy
}

/// <summary>
/// Raised after a connected provider's stored token changed in any way — renewed, refused, signed in
/// again or disconnected — with the owner and the provider id. Running sessions hold a copy of the
/// token in their credential store and need the new state.
/// </summary>
public interface IGitTokenChanges
{
    event Action<string, string>? TokensChanged;
}

public sealed class GitOAuthRefreshOptions
{
    /// <summary>
    /// How long before expiry a token is renewed. GitLab issues two-hour access tokens; a quarter of
    /// an hour ahead leaves room for a failed pass and several retries before a running session
    /// holds an expired token.
    /// </summary>
    public TimeSpan Margin { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a session start waits for another caller's refresh of the same grant
    /// instead of spending the refresh token a second time.</summary>
    public TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(20);

    public bool Enabled { get; set; } = true;
}

public sealed class GitAuthService : IGitAuthService, IGitTokenChanges
{
    private const int MaxWriteAttempts = 5;

    private readonly List<GitProviderConfig> _providers;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GitAuthService> _log;
    private readonly IGitTokenStore _tokens;
    private readonly IProviderLoginLock _locks;
    private readonly GitOAuthRefreshOptions _options;
    private readonly TimeProvider _time;
    private readonly byte[] _stateKey;

    public event Action<string, string>? TokensChanged;

    public GitAuthService(IConfiguration cfg, IHttpClientFactory http, ILogger<GitAuthService> log,
        IGitTokenStore tokens, IProviderLoginLock locks, GitOAuthRefreshOptions options, TimeProvider? time = null)
    {
        _http = http;
        _log = log;
        _tokens = tokens;
        _locks = locks;
        _options = options;
        _time = time ?? TimeProvider.System;
        _providers = cfg.GetSection("Git:Providers").Get<List<GitProviderConfig>>() ?? new();
        _providers = _providers.Where(p => !string.IsNullOrWhiteSpace(p.Id)
            && !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.ClientSecret)).ToList();

        // Signed OAuth state (owner|provider|nonce|exp). Shared key so the browser
        // redirect callback verifies across replicas. Falls back to an ephemeral key
        // (single-replica dev) with a warning.
        var key = cfg["Git:StateKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            if (_providers.Count > 0)
                _log.LogWarning("Git:StateKey is not configured; using an ephemeral key. OAuth connect will fail across multiple replicas or restarts.");
        }
        _stateKey = Encoding.UTF8.GetBytes(key);
    }

    public bool AnyConfigured => _providers.Count > 0;

    private GitProviderConfig? Provider(string id) => _providers.FirstOrDefault(p => p.Id == id);

    public bool IsConfigured(string providerId) => Provider(providerId) is not null;

    public async Task<bool> IsConnectedAsync(string owner, string providerId, CancellationToken ct = default)
    {
        if (Provider(providerId) is null) return false;
        var grant = GitGrant.Read((await ReadAsync(owner, ct)).Data, providerId);
        return grant is { Rejected: false };
    }

    public async Task<IReadOnlyList<GitProviderInfo>> ListProvidersAsync(string owner, CancellationToken ct = default)
    {
        var data = (await ReadAsync(owner, ct)).Data;
        return _providers.Select(p =>
        {
            var grant = GitGrant.Read(data, p.Id);
            return new GitProviderInfo
            {
                Id = p.Id, Type = p.Kind, DisplayName = p.DisplayName,
                Connected = grant is not null,
                NeedsSignIn = grant?.Rejected == true,
                Username = grant?.Username
            };
        }).ToList();
    }

    public string CreateAuthorizeUrl(string providerId, string owner, string redirectUri)
    {
        var p = Provider(providerId) ?? throw new ArgumentException($"Unknown provider '{providerId}'.");
        var state = SignState(owner, providerId);
        var q = new Dictionary<string, string?>
        {
            ["client_id"] = p.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.IsNullOrWhiteSpace(p.Scopes) ? p.DefaultScopes : p.Scopes,
            ["state"] = state
        };
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(p.AuthorizeUrl, q);
    }

    public async Task<string?> HandleCallbackAsync(string providerId, string code, string state, string redirectUri, CancellationToken ct = default)
    {
        var p = Provider(providerId);
        if (p is null || !VerifyState(state, providerId, out var owner)) return null;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = p.ClientId,
            ["client_secret"] = p.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        };
        var exchange = await ExchangeAsync(p, form, ct);
        if (exchange.Tokens is not { } token) return null;

        var username = await FetchUsernameAsync(p, token.Access, ct);
        // A new sign-in replaces whatever is stored, a refused grant included.
        var stored = await ChangeAsync(owner, data =>
        {
            GitGrant.Write(data, p.Id, token, username);
            return true;
        }, ct);
        if (!stored) return null;
        // Sessions running on the refused grant pick up the new one without a restart.
        Raise(owner, p.Id);
        return owner;
    }

    public async Task DisconnectAsync(string owner, string providerId, CancellationToken ct = default)
    {
        var removed = await ChangeAsync(owner, data =>
        {
            var keys = data.Keys.Where(k => k.StartsWith($"{providerId}.")).ToList();
            foreach (var k in keys) data.Remove(k);
            return keys.Count > 0;
        }, ct);
        // Running sessions lose the token now rather than keep it until they end. Only a configured
        // provider has sessions to tell; the keys of one removed from the configuration still go.
        if (removed && Provider(providerId) is { } p) Raise(owner, p.Id);
    }

    public async Task<IReadOnlyList<GitProject>> SearchProjectsAsync(string owner, string providerId, string? query, CancellationToken ct = default)
    {
        var p = Provider(providerId) ?? throw new ArgumentException($"Unknown provider '{providerId}'.");
        var token = await GetValidTokenAsync(owner, p, ct)
            ?? throw new InvalidOperationException($"{p.DisplayName} is not connected.");
        var q = (query ?? "").Trim();
        var client = _http.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("open-agenthub");

        if (p.Kind == "github")
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var url = $"{p.ApiBase}/user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<GitProject>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.EnumerateArray()
                .Select(e => new GitProject
                {
                    Name = e.GetProperty("name").GetString() ?? "",
                    FullName = e.GetProperty("full_name").GetString() ?? "",
                    Url = e.GetProperty("clone_url").GetString() ?? "",
                    DefaultBranch = e.TryGetProperty("default_branch", out var b) ? b.GetString() : null,
                    ProviderId = p.Id
                })
                .Where(r => q.Length == 0 || r.FullName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(50).ToList();
        }
        else // gitlab
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var url = $"{p.ApiBase}/projects?membership=true&simple=true&per_page=50&order_by=last_activity_at" +
                      (q.Length > 0 ? $"&search={Uri.EscapeDataString(q)}" : "");
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<GitProject>();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.EnumerateArray().Select(e => new GitProject
            {
                Name = e.GetProperty("path").GetString() ?? "",
                FullName = e.GetProperty("path_with_namespace").GetString() ?? "",
                Url = e.GetProperty("http_url_to_repo").GetString() ?? "",
                DefaultBranch = e.TryGetProperty("default_branch", out var b) ? b.GetString() : null,
                ProviderId = p.Id
            }).ToList();
        }
    }

    public async Task<string?> BuildCredentialStoreAsync(string owner, IEnumerable<RepoRef> repos, CancellationToken ct = default)
    {
        var lines = new List<string>();
        foreach (var pid in repos.Select(r => r.ProviderId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            var p = Provider(pid!);
            if (p is null) continue;
            var token = await GetValidTokenAsync(owner, p, ct);
            if (token is null) continue;
            lines.Add($"https://{Uri.EscapeDataString(p.GitCredUser)}:{Uri.EscapeDataString(token)}@{p.GitHost}");
        }
        return lines.Count == 0 ? null : string.Join("\n", lines) + "\n";
    }

    // ---- token handling -------------------------------------------------------

    private bool IsDue(GitGrant grant) =>
        !grant.Rejected && grant.Refresh is not null
        && grant.Expires is { } expires && expires - _time.GetUtcNow() < _options.Margin;

    /// <summary>
    /// The access token to hand out, renewed first when it is due. A refused grant yields null: its
    /// token is dead, and a store line holding it only makes git and glab fail against the provider
    /// again on every command.
    /// </summary>
    private async Task<string?> GetValidTokenAsync(string owner, GitProviderConfig p, CancellationToken ct)
    {
        var grant = GitGrant.Read((await ReadAsync(owner, ct)).Data, p.Id);
        if (grant is null || grant.Rejected) return null;
        if (!IsDue(grant)) return grant.Access;

        await RefreshIfDueAsync(owner, p.Id, waitForLock: true, ct);
        // Re-read whatever the outcome: a refresh by another replica, or by the caller this one
        // waited for, lands in the store just the same.
        grant = GitGrant.Read((await ReadAsync(owner, ct)).Data, p.Id);
        return grant is { Rejected: false } ? grant.Access : null;
    }

    public async Task<GitRefreshOutcome> RefreshIfDueAsync(string owner, string providerId, bool waitForLock,
        CancellationToken ct = default)
    {
        var p = Provider(providerId);
        if (p is null) return GitRefreshOutcome.NotDue;
        var key = $"git-oauth/{OwnerKey(owner)}/{p.Id}";
        await using var held = waitForLock ? await AcquireWaitingAsync(key, ct) : await _locks.TryAcquireAsync(key, ct);
        if (held is null) return GitRefreshOutcome.Busy;

        // Re-read under the lock: whoever held it before may just have renewed this grant.
        var grant = GitGrant.Read((await ReadAsync(owner, ct)).Data, p.Id);
        if (grant is null || !IsDue(grant)) return GitRefreshOutcome.NotDue;
        var spent = grant.Refresh!;

        var result = await ExchangeAsync(p, new Dictionary<string, string>
        {
            ["client_id"] = p.ClientId, ["client_secret"] = p.ClientSecret,
            ["grant_type"] = "refresh_token", ["refresh_token"] = spent
        }, ct);

        if (result.Tokens is { } renewed)
        {
            // GitLab rotates the refresh token, and the one just spent is dead from this answer on.
            // Losing the write would leave the secret holding only that dead token, so a conflicting
            // write is re-read and retried rather than dropped.
            var stored = await ChangeAsync(owner, data =>
            {
                if (GitGrant.Read(data, p.Id)?.Refresh != spent) return false;
                GitGrant.Write(data, p.Id, renewed with { Refresh = renewed.Refresh ?? spent }, username: null);
                return true;
            }, ct);
            if (!stored)
            {
                // A sign-in in the meantime replaced the grant; that one is the owner's choice.
                _log.LogInformation("A {Provider} grant changed while it was being renewed; kept the newer one", p.Id);
                return GitRefreshOutcome.NotDue;
            }
            _log.LogInformation("Renewed a {Provider} token", p.Id);
            Raise(owner, p.Id);
            return GitRefreshOutcome.Refreshed;
        }

        if (result.Rejected)
        {
            var marked = await ChangeAsync(owner, data =>
            {
                if (GitGrant.Read(data, p.Id)?.Refresh != spent) return false;
                data[$"{p.Id}.rejected"] = Encoding.UTF8.GetBytes(_time.GetUtcNow().ToUnixTimeSeconds().ToString());
                return true;
            }, ct);
            if (!marked) return GitRefreshOutcome.NotDue;
            _log.LogWarning("{Provider} refused to renew a token; the owner has to sign in again", p.Id);
            Raise(owner, p.Id);
            return GitRefreshOutcome.Rejected;
        }

        return GitRefreshOutcome.Failed;
    }

    private async Task<IAsyncDisposable?> AcquireWaitingAsync(string key, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _options.LockWait;
        while (true)
        {
            var held = await _locks.TryAcquireAsync(key, ct);
            if (held is not null || _time.GetUtcNow() >= deadline) return held;
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
    }

    private void Raise(string owner, string providerId) => TokensChanged?.Invoke(owner, providerId);

    private Task<GitTokenSnapshot> ReadAsync(string owner, CancellationToken ct) => _tokens.ReadAsync(OwnerKey(owner), ct);

    /// <summary>
    /// Applies <paramref name="change"/> (false: nothing to write) to a fresh copy of the owner's
    /// secret and writes it conditionally, re-reading on a conflicting write.
    /// </summary>
    private async Task<bool> ChangeAsync(string owner, Func<Dictionary<string, byte[]>, bool> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var snapshot = await ReadAsync(owner, ct);
            var data = new Dictionary<string, byte[]>(snapshot.Data);
            if (!change(data)) return false;
            if (await _tokens.TryWriteAsync(snapshot, data, ct)) return true;
        }
        _log.LogWarning("Writing a git provider secret kept conflicting; gave up after {Attempts} attempts", MaxWriteAttempts);
        return false;
    }

    private sealed record ExchangeResult(GitTokenSet? Tokens, bool Rejected);

    private async Task<ExchangeResult> ExchangeAsync(GitProviderConfig p, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("open-agenthub");
            using var content = new FormUrlEncodedContent(form);
            using var resp = await client.PostAsync(p.TokenUrl, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            // Only the status and the OAuth error code are logged: the body of a success is the token.
            if (!resp.IsSuccessStatusCode)
            {
                var error = OAuthError(body);
                _log.LogWarning("Token request to {P} failed: {Code} {Error}", p.Id, (int)resp.StatusCode, error ?? "-");
                // RFC 6749 5.2: invalid_grant is the one answer that says the refresh token itself is
                // dead (revoked, expired, already used). invalid_client and the rest are about the
                // app's configuration, which a new sign-in by the owner would not fix.
                return new ExchangeResult(null, error == "invalid_grant");
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at) || at.GetString() is not { Length: > 0 } access)
            {
                _log.LogWarning("Token request to {P} returned no access_token", p.Id);
                return new ExchangeResult(null, false);
            }
            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            DateTimeOffset? exp = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var secs)
                ? _time.GetUtcNow().AddSeconds(secs) : null;
            return new ExchangeResult(new GitTokenSet(access, string.IsNullOrEmpty(refresh) ? null : refresh, exp), false);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogWarning("Token request to {P} failed: {Reason}", p.Id, error.GetType().Name);
            return new ExchangeResult(null, false);
        }
    }

    private static string? OAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Bounded and restricted to the RFC's code alphabet, since it goes into the log.
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                && e.GetString() is { Length: > 0 and <= 64 } code && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                ? code : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<string?> FetchUsernameAsync(GitProviderConfig p, string token, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("open-agenthub");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using var resp = await client.GetAsync($"{p.ApiBase}/user", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return p.Kind == "github"
                ? doc.RootElement.GetProperty("login").GetString()
                : doc.RootElement.GetProperty("username").GetString();
        }
        catch { return null; }
    }

    // ---- state signing --------------------------------------------------------

    private string SignState(string owner, string providerId)
    {
        var payload = $"{owner}|{providerId}|{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}|{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}";
        var sig = Convert.ToHexString(new HMACSHA256(_stateKey).ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return Base64Url(Encoding.UTF8.GetBytes($"{payload}|{sig}"));
    }

    private bool VerifyState(string state, string providerId, out string owner)
    {
        owner = "";
        try
        {
            var decoded = Encoding.UTF8.GetString(Base64UrlDecode(state));
            var parts = decoded.Split('|');
            if (parts.Length != 5) return false;
            var payload = string.Join('|', parts[..4]);
            var expected = Convert.ToHexString(new HMACSHA256(_stateKey).ComputeHash(Encoding.UTF8.GetBytes(payload)));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[4]))) return false;
            if (parts[1] != providerId) return false;
            if (DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[3])) < DateTimeOffset.UtcNow) return false;
            owner = parts[0];
            return true;
        }
        catch { return false; }
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>The owner label value; also what <c>gitauth-&lt;key&gt;</c> is named after.</summary>
    public static string OwnerKey(string owner)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16].ToLowerInvariant();
        return $"u-{hash}";
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        return Convert.FromBase64String(s);
    }
}

public sealed record GitTokenSet(string Access, string? Refresh, DateTimeOffset? Expires);

/// <summary>One provider's entry in the owner's secret: <c>&lt;id&gt;.access</c>, <c>.refresh</c>,
/// <c>.expires</c> (unix seconds), <c>.username</c> and, once the provider refused the refresh
/// token, <c>.rejected</c> (unix seconds).</summary>
public sealed record GitGrant(string Access, string? Refresh, DateTimeOffset? Expires, string? Username, bool Rejected)
{
    public static GitGrant? Read(IReadOnlyDictionary<string, byte[]> data, string providerId)
    {
        if (!data.TryGetValue($"{providerId}.access", out var access)) return null;
        string? Text(string key) => data.TryGetValue($"{providerId}.{key}", out var v) ? Encoding.UTF8.GetString(v) : null;
        var expires = long.TryParse(Text("expires"), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : (DateTimeOffset?)null;
        return new GitGrant(Encoding.UTF8.GetString(access), Text("refresh"), expires, Text("username"),
            data.ContainsKey($"{providerId}.rejected"));
    }

    /// <summary>Writes a token set and clears a refusal; a null username keeps the stored one.</summary>
    public static void Write(Dictionary<string, byte[]> data, string providerId, GitTokenSet tokens, string? username)
    {
        data[$"{providerId}.access"] = Encoding.UTF8.GetBytes(tokens.Access);
        if (tokens.Refresh is not null) data[$"{providerId}.refresh"] = Encoding.UTF8.GetBytes(tokens.Refresh);
        else data.Remove($"{providerId}.refresh");
        if (tokens.Expires is { } exp) data[$"{providerId}.expires"] = Encoding.UTF8.GetBytes(exp.ToUnixTimeSeconds().ToString());
        else data.Remove($"{providerId}.expires");
        if (username is not null) data[$"{providerId}.username"] = Encoding.UTF8.GetBytes(username);
        data.Remove($"{providerId}.rejected");
    }
}
