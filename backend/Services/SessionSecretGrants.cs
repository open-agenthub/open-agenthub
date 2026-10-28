using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

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

    /// <summary>
    /// The owner's answer to this session's own secret request: a non-inheritable "requested"
    /// grant, and the secret added to the session's ceiling when it has one. Returns whether the
    /// record changed.
    /// </summary>
    /// <remarks>
    /// A ceiling keeps a restricted token or a spawning parent from handing a session more than
    /// it held itself. Every session an agent spawns has one, and a secret typed in to answer a
    /// request is new, so it was never inside it: before #330 the answer was refused with
    /// secret_not_allowed and no session started by another one could ever get what it asked
    /// for. Only the signed-in owner answers a request, for this session and this secret alone;
    /// widening the ceiling by exactly that keeps every other limit where it was, and a session
    /// this one spawns later still gets at most what it holds.
    /// </remarks>
    public static bool AnswerRequest(SessionRecord session, string secretId)
    {
        var changed = false;
        var grants = Parse(session.SecretGrantIdsJson).ToList();
        if (!grants.Any(g => g.SecretId == secretId))
        {
            grants.Add(new(secretId, false, "requested"));
            session.SecretGrantIdsJson = Serialize(grants);
            changed = true;
        }
        if (SessionSecretSelection.Parse(session.SecretCeilingJson) is { } ceiling && !ceiling.Contains(secretId))
        {
            session.SecretCeilingJson = SessionSecretSelection.Serialize([.. ceiling, secretId]);
            changed = true;
        }
        return changed;
    }
}
