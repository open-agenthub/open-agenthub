using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using k8s;
using k8s.Models;

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
    /// <summary>git-credentials file content (one line per provider host) for the repos'
    /// providers, or null if none of them use a connected OAuth provider.</summary>
    Task<string?> BuildCredentialStoreAsync(string owner, IEnumerable<RepoRef> repos, CancellationToken ct = default);
}

public sealed class GitAuthService : IGitAuthService
{
    private readonly List<GitProviderConfig> _providers;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GitAuthService> _log;
    private readonly Kubernetes _k8s;
    private readonly string _ns;
    private readonly byte[] _stateKey;

    private const string OwnerLabel = "agenthub.dev/owner";

    public GitAuthService(IConfiguration cfg, IHttpClientFactory http, ILogger<GitAuthService> log)
    {
        _http = http;
        _log = log;
        _providers = cfg.GetSection("Git:Providers").Get<List<GitProviderConfig>>() ?? new();
        _providers = _providers.Where(p => !string.IsNullOrWhiteSpace(p.Id)
            && !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.ClientSecret)).ToList();
        _ns = cfg["AgentHub:Namespace"] ?? "agenthub";

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

        var kc = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8s = new Kubernetes(kc);
    }

    public bool AnyConfigured => _providers.Count > 0;

    private GitProviderConfig? Provider(string id) => _providers.FirstOrDefault(p => p.Id == id);

    public async Task<IReadOnlyList<GitProviderInfo>> ListProvidersAsync(string owner, CancellationToken ct = default)
    {
        var secret = await ReadSecretOrNullAsync(SecretName(owner), ct);
        var data = secret?.Data ?? new Dictionary<string, byte[]>();
        return _providers.Select(p => new GitProviderInfo
        {
            Id = p.Id, Type = p.Kind, DisplayName = p.DisplayName,
            Connected = data.ContainsKey($"{p.Id}.access"),
            Username = data.TryGetValue($"{p.Id}.username", out var u) ? Encoding.UTF8.GetString(u) : null
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
        var token = await ExchangeAsync(p, form, ct);
        if (token is null) return null;

        var username = await FetchUsernameAsync(p, token.Value.access, ct);
        await StoreTokenAsync(owner, p, token.Value, username, ct);
        return owner;
    }

    public async Task DisconnectAsync(string owner, string providerId, CancellationToken ct = default)
    {
        var name = SecretName(owner);
        var secret = await ReadSecretOrNullAsync(name, ct);
        if (secret?.Data is null) return;
        var data = new Dictionary<string, byte[]>(secret.Data);
        foreach (var k in data.Keys.Where(k => k.StartsWith($"{providerId}.")).ToList())
            data.Remove(k);
        secret.Data = data;
        await _k8s.CoreV1.ReplaceNamespacedSecretAsync(secret, name, _ns, cancellationToken: ct);
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

    private record struct TokenSet(string access, string? refresh, DateTimeOffset? expires);

    private async Task<TokenSet?> ExchangeAsync(GitProviderConfig p, Dictionary<string, string> form, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("open-agenthub");
        using var resp = await client.PostAsync(p.TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("Token exchange with {P} failed: {Code}", p.Id, resp.StatusCode); return null; }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at)) { _log.LogWarning("Token exchange with {P} returned no access_token", p.Id); return null; }
        var access = at.GetString()!;
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTimeOffset? exp = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var secs)
            ? DateTimeOffset.UtcNow.AddSeconds(secs) : null;
        return new TokenSet(access, refresh, exp);
    }

    private async Task<string?> GetValidTokenAsync(string owner, GitProviderConfig p, CancellationToken ct)
    {
        var secret = await ReadSecretOrNullAsync(SecretName(owner), ct);
        if (secret?.Data is not { } data || !data.TryGetValue($"{p.Id}.access", out var accessBytes))
            return null;
        var access = Encoding.UTF8.GetString(accessBytes);
        var expires = data.TryGetValue($"{p.Id}.expires", out var e)
            ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(Encoding.UTF8.GetString(e))) : (DateTimeOffset?)null;

        // Refresh if it expires within two minutes and we have a refresh token (GitLab).
        if (expires is { } exp && exp <= DateTimeOffset.UtcNow.AddMinutes(2)
            && data.TryGetValue($"{p.Id}.refresh", out var rb))
        {
            var refreshed = await ExchangeAsync(p, new Dictionary<string, string>
            {
                ["client_id"] = p.ClientId, ["client_secret"] = p.ClientSecret,
                ["grant_type"] = "refresh_token", ["refresh_token"] = Encoding.UTF8.GetString(rb)
            }, ct);
            if (refreshed is not null)
            {
                var username = data.TryGetValue($"{p.Id}.username", out var u) ? Encoding.UTF8.GetString(u) : null;
                await StoreTokenAsync(owner, p, refreshed.Value, username, ct);
                return refreshed.Value.access;
            }
        }
        return access;
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

    private async Task StoreTokenAsync(string owner, GitProviderConfig p, TokenSet t, string? username, CancellationToken ct)
    {
        var name = SecretName(owner);
        var secret = await ReadSecretOrNullAsync(name, ct);
        var data = secret?.Data is { } d ? new Dictionary<string, byte[]>(d) : new Dictionary<string, byte[]>();
        data[$"{p.Id}.access"] = Encoding.UTF8.GetBytes(t.access);
        if (t.refresh is not null) data[$"{p.Id}.refresh"] = Encoding.UTF8.GetBytes(t.refresh);
        if (t.expires is { } exp) data[$"{p.Id}.expires"] = Encoding.UTF8.GetBytes(exp.ToUnixTimeSeconds().ToString());
        if (username is not null) data[$"{p.Id}.username"] = Encoding.UTF8.GetBytes(username);

        if (secret is null)
        {
            await _k8s.CoreV1.CreateNamespacedSecretAsync(new V1Secret
            {
                Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = _ns, Labels = new Dictionary<string, string> { [OwnerLabel] = Sanitize(owner) } },
                Type = "Opaque", Data = data
            }, _ns, cancellationToken: ct);
        }
        else
        {
            secret.Data = data;
            await _k8s.CoreV1.ReplaceNamespacedSecretAsync(secret, name, _ns, cancellationToken: ct);
        }
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

    private static string SecretName(string owner) => $"gitauth-{Sanitize(owner)}";

    private static string Sanitize(string owner)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16].ToLowerInvariant();
        return $"u-{hash}";
    }

    private async Task<V1Secret?> ReadSecretOrNullAsync(string name, CancellationToken ct)
    {
        try { return await _k8s.CoreV1.ReadNamespacedSecretAsync(name, _ns, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        return Convert.FromBase64String(s);
    }
}
