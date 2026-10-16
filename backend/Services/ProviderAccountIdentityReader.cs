using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// Pulls a display identity out of a provider's credential file, or out of the header the Claude
/// watcher sends alongside its upload. Everything here is best effort and unverified by design
/// (docs/provider-accounts.md, "Identity is display only"): a JWT is decoded, never validated,
/// and any shape that does not match yields null rather than an error — an account without an
/// identity is still a working account.
/// </summary>
public static class ProviderAccountIdentityReader
{
    public const string HeaderName = "X-Agent-Identity";
    public const int MaxHeaderChars = 4096;
    private const int MaxFieldLength = 200;

    public static ProviderAccountIdentity? FromFile(AgentKind agent, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return agent switch
            {
                AgentKind.Codex => FromCodex(root),
                AgentKind.Cursor => FromCursor(root),
                AgentKind.OpenClaw => FromOpenClaw(root),
                AgentKind.OpenCode => FromOpenCode(root),
                // Claude's file has opaque tokens only; its identity arrives in the header.
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes the <c>X-Agent-Identity</c> header: base64url of <c>{key, email, organization}</c>.
    /// Base64 because an organisation name is not guaranteed to be header-safe ASCII.
    /// </summary>
    public static ProviderAccountIdentity? FromHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > MaxHeaderChars) return null;
        try
        {
            var padded = header.Trim().Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(padded));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return Normalize(Text(root, "key"), Text(root, "email"), Text(root, "organization"));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static ProviderAccountIdentity? FromCodex(JsonElement root)
    {
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return null;
        var payload = JwtPayload(Text(tokens, "id_token"));
        if (payload is null) return null;
        var claims = payload.Value;
        var email = Text(claims, "email");
        string? accountId = null;
        if (claims.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
            accountId = Text(auth, "chatgpt_account_id");
        return Normalize(accountId ?? email ?? Text(claims, "sub"), email, null);
    }

    private static ProviderAccountIdentity? FromCursor(JsonElement root)
    {
        var payload = JwtPayload(Text(root, "accessToken"));
        if (payload is null) return null;
        var claims = payload.Value;
        return Normalize(Text(claims, "sub"), Text(claims, "email"), null);
    }

    private static ProviderAccountIdentity? FromOpenClaw(JsonElement root)
    {
        if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object) return null;
        var names = new List<string>();
        string? email = null;
        foreach (var profile in profiles.EnumerateObject())
        {
            names.Add(profile.Name);
            if (email is null && profile.Value.ValueKind == JsonValueKind.Object)
                email = Text(profile.Value, "email");
        }
        if (names.Count == 0) return null;
        names.Sort(StringComparer.Ordinal);
        return Normalize(string.Join(",", names), email, names.Count == 1 ? names[0] : null);
    }

    /// <summary>
    /// OpenCode's auth.json maps provider ids to { type, ... } and names no person. An OpenCode Go
    /// login is an API key, so two logins to the same provider differ only in their key: the
    /// matching key therefore carries a short hash of each api key (an oauth entry's accountId,
    /// when it has one — its tokens rotate), otherwise a second Go key would be matched to the
    /// first account by provider name and overwrite it. The provider list is what is shown.
    /// The hash never leaves the secret: <see cref="ProviderAccountInfo"/> does not carry the key.
    /// </summary>
    private static ProviderAccountIdentity? FromOpenCode(JsonElement root)
    {
        var parts = new List<string>();
        var providers = new List<string>();
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            providers.Add(entry.Name);
            var distinct = Text(entry.Value, "type") switch
            {
                "api" => Text(entry.Value, "key") is { Length: > 0 } key ? Fingerprint(key) : null,
                "oauth" => Text(entry.Value, "accountId"),
                _ => null
            };
            parts.Add(distinct is null ? entry.Name : $"{entry.Name}:{distinct}");
        }
        if (providers.Count == 0) return null;
        parts.Sort(StringComparer.Ordinal);
        providers.Sort(StringComparer.Ordinal);
        return Normalize(string.Join(",", parts), null, string.Join(", ", providers));
    }

    private static string Fingerprint(string secret) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..12]
            .ToLowerInvariant();

    private static JsonElement? JwtPayload(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ProviderAccountIdentity? Normalize(string? key, string? email, string? organization)
    {
        var identity = new ProviderAccountIdentity(Clean(key), Clean(email), Clean(organization));
        return identity.IsEmpty ? null : identity;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
            if (!char.IsControl(ch)) builder.Append(ch);
        if (builder.Length == 0) return null;
        return builder.Length > MaxFieldLength ? builder.ToString(0, MaxFieldLength) : builder.ToString();
    }
}
