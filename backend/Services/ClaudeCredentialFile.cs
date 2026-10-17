using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHub.Api.Services;

/// <summary>
/// Reads and renews Claude Code's <c>.credentials.json</c> (<c>{"claudeAiOauth": {accessToken,
/// refreshToken, expiresAt, scopes, ...}}</c>, pinned 2.1.287) without ever handing a token value
/// to anything that logs. Why the hub reads the file at all is in docs/provider-accounts.md,
/// "The hub owns each login".
/// </summary>
public static class ClaudeCredentialFile
{
    /// <summary>What the hub needs from a file: never the access token itself.</summary>
    public sealed record State(string? RefreshToken, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Scopes,
        string? ClientId, bool IsBlank);

    public static State? Read(byte[] file)
    {
        try
        {
            if (JsonNode.Parse(file) is not JsonObject root || root["claudeAiOauth"] is not JsonObject oauth) return null;
            var access = oauth["accessToken"] is JsonValue a && a.TryGetValue<string>(out var at) ? at : null;
            var refresh = oauth["refreshToken"] is JsonValue r && r.TryGetValue<string>(out var rt) ? rt : null;
            DateTimeOffset? expires = oauth["expiresAt"] is JsonValue e && e.TryGetValue<long>(out var ms) && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            var scopes = oauth["scopes"] is JsonArray list
                ? list.Select(s => s is JsonValue v && v.TryGetValue<string>(out var text) ? text : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
                : new List<string>();
            var clientId = oauth["clientId"] is JsonValue c && c.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;
            // The CLI writes empty tokens and expiresAt 0 over its own file when a refresh is
            // answered with invalid_grant. Such a file names a login that no longer exists.
            var blank = string.IsNullOrEmpty(access) || refresh == "";
            return new State(string.IsNullOrEmpty(refresh) ? null : refresh, expires, scopes, clientId, blank);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsBlank(byte[] file) => Read(file) is not { IsBlank: false };

    /// <summary>
    /// True when <paramref name="incoming"/> is an older generation of the login in
    /// <paramref name="stored"/>. A refresh always moves expiresAt forward, so of two files of
    /// the same login the one expiring later was issued later; the refresh token of the older one
    /// has already been spent. Files that do not say when they expire are never called stale.
    /// </summary>
    public static bool IsOlder(byte[] incoming, byte[] stored)
    {
        var next = Read(incoming);
        var current = Read(stored);
        if (next?.ExpiresAt is not { } nextExpiry || current?.ExpiresAt is not { } currentExpiry) return false;
        if (current.IsBlank) return false;
        return nextExpiry < currentExpiry;
    }

    /// <summary>
    /// The stored file with the tokens of a refresh response. Every other field — the plan, the
    /// rate-limit tier, whatever a later CLI adds — is kept as it was, because the CLI reads
    /// them from this file and the hub has no business dropping what it does not understand.
    /// </summary>
    public static byte[] WithRefreshedTokens(byte[] stored, ClaudeTokenResponse tokens, DateTimeOffset now)
    {
        var root = JsonNode.Parse(stored) as JsonObject
            ?? throw new ArgumentException("Not a Claude credential file.", nameof(stored));
        var oauth = root["claudeAiOauth"] as JsonObject
            ?? throw new ArgumentException("Not a Claude credential file.", nameof(stored));
        oauth["accessToken"] = tokens.AccessToken;
        // The CLI keeps the refresh token it redeemed when the response carries none; so does the hub.
        if (!string.IsNullOrEmpty(tokens.RefreshToken)) oauth["refreshToken"] = tokens.RefreshToken;
        oauth["expiresAt"] = now.AddSeconds(tokens.ExpiresInSeconds).ToUnixTimeMilliseconds();
        if (tokens.RefreshTokenExpiresInSeconds is { } refreshLifetime)
            oauth["refreshTokenExpiresAt"] = now.AddSeconds(refreshLifetime).ToUnixTimeMilliseconds();
        if (tokens.Scopes is { Count: > 0 } scopes)
            oauth["scopes"] = new JsonArray(scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
