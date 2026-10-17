using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHub.Api.Services;

/// <summary>A successful refresh. The values go into the credential file and nowhere else.</summary>
public sealed record ClaudeTokenResponse(string AccessToken, string? RefreshToken, long ExpiresInSeconds,
    long? RefreshTokenExpiresInSeconds, IReadOnlyList<string>? Scopes);

public enum ClaudeRefreshStatus
{
    Refreshed,
    /// <summary>The provider no longer accepts the refresh token: spent by someone else, revoked, or expired.</summary>
    Rejected,
    /// <summary>Network trouble, a 5xx, a malformed answer: worth another try on the next pass.</summary>
    Failed
}

public sealed record ClaudeRefreshResult(ClaudeRefreshStatus Status, ClaudeTokenResponse? Tokens = null, string? Detail = null);

public interface IClaudeTokenEndpoint
{
    Task<ClaudeRefreshResult> RefreshAsync(string refreshToken, IReadOnlyList<string> scopes, string? clientId,
        CancellationToken ct);
}

/// <summary>
/// The OAuth token endpoint Claude Code itself refreshes against, called the way the pinned CLI
/// calls it (2.1.287: a JSON body with grant_type, refresh_token, client_id and the space-joined
/// scopes). The hub refreshes on the login's behalf so that one party, not every session, spends
/// each refresh token (docs/provider-accounts.md, "The hub owns each login").
/// </summary>
public sealed class ClaudeTokenEndpoint : IClaudeTokenEndpoint
{
    // Both read from the pinned CLI. The client id is the CLI's public one: the tokens being
    // refreshed were issued to it, and a refresh under another client id is refused.
    public const string DefaultTokenUrl = "https://platform.claude.com/v1/oauth/token";
    public const string DefaultClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private readonly HttpClient _http;
    private readonly string _tokenUrl;
    private readonly string _clientId;

    public ClaudeTokenEndpoint(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
        var section = configuration.GetSection("AgentHub:ClaudeLoginRefresh");
        _tokenUrl = string.IsNullOrWhiteSpace(section["TokenUrl"]) ? DefaultTokenUrl : section["TokenUrl"]!;
        _clientId = string.IsNullOrWhiteSpace(section["ClientId"]) ? DefaultClientId : section["ClientId"]!;
    }

    public async Task<ClaudeRefreshResult> RefreshAsync(string refreshToken, IReadOnlyList<string> scopes,
        string? clientId, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId ?? _clientId
        };
        if (scopes.Count > 0) body["scope"] = string.Join(' ', scopes);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(_tokenUrl, body, ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(ClaudeRefreshStatus.Failed, Detail: error.GetType().Name);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                var error = await ReadErrorAsync(response, ct);
                // invalid_grant is the one answer that means the login is gone. Anything else at
                // 400 (invalid_scope, a malformed request after a provider change) is retried, so a
                // hub-side mistake never marks a working login as needing a new sign-in.
                return error == "invalid_grant"
                    ? new(ClaudeRefreshStatus.Rejected, Detail: error)
                    : new(ClaudeRefreshStatus.Failed, Detail: $"HTTP {(int)response.StatusCode} {error}");
            }
            if (!response.IsSuccessStatusCode)
                return new(ClaudeRefreshStatus.Failed, Detail: $"HTTP {(int)response.StatusCode}");

            TokenBody? parsed;
            try { parsed = await response.Content.ReadFromJsonAsync<TokenBody>(ct); }
            catch (JsonException) { return new(ClaudeRefreshStatus.Failed, Detail: "malformed response"); }
            if (parsed is not { AccessToken.Length: > 0, ExpiresIn: > 0 })
                return new(ClaudeRefreshStatus.Failed, Detail: "incomplete response");
            var granted = parsed.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(ClaudeRefreshStatus.Refreshed, new ClaudeTokenResponse(parsed.AccessToken, parsed.RefreshToken,
                parsed.ExpiresIn, parsed.RefreshTokenExpiresIn, granted));
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            // Only the error code, and only in its OAuth shape (a short snake_case word); the
            // description can echo request data and stays out of the log.
            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() is { Length: > 0 and <= 64 } code && code.All(c => c is (>= 'a' and <= 'z') or '_')
                ? code
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TokenBody(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("refresh_token_expires_in")] long? RefreshTokenExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
}
