using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public static class SessionSecretGrants
{
    public static IReadOnlyList<StoredSessionSecretGrant> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            // Existing request grants were strings; migrating on write keeps their origin and
            // prevents old grants from silently becoming inheritable after an upgrade.
            return doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
                ? new StoredSessionSecretGrant(e.GetString()!, false, "requested")
                : e.Deserialize<StoredSessionSecretGrant>()!).Where(g => g is not null).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static IReadOnlyList<string> Ids(string? json) => Parse(json).Select(g => g.SecretId).ToList();
    public static string Serialize(IEnumerable<StoredSessionSecretGrant> grants) => JsonSerializer.Serialize(grants);
}
