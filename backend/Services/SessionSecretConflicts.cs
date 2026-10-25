using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// Refused because one session would receive two secrets that land in the same place. An
/// <see cref="ArgumentException"/>, so every edge that already turns a validation error into a
/// 400 or an MCP <c>invalid_argument</c> passes the message on unchanged.
/// </summary>
public sealed class SessionSecretConflictException(string message) : ArgumentException(message)
{
    /// <summary>The code token-authenticated callers get next to the sentence: the stdio MCP server
    /// answers with backend codes only, never with a message text.</summary>
    public const string Code = "secret_conflict";

    public object Body => new { code = Code, error = Message };
}

/// <summary>
/// Secret names are not unique in the store (#253); what has to be unique is where a secret lands
/// in one session: a key of the session's file copy (<c>~/.secrets/NAME</c>) or of its variable
/// copy. Two secrets with the same name and delivery in one session would be one dictionary key,
/// and the last one written would silently win — the agent would get a value, just maybe not the
/// one the owner meant. So a set with such a pair is refused wherever a session's set is decided:
/// creating or editing a session, a project's selection, renaming or re-delivering a secret, and
/// answering a secret request.
///
/// Compared ordinally: <c>API_KEY</c> and <c>api_key</c> are two variables and two files on the
/// pod's filesystem and two keys of a Kubernetes secret, so they do not collide. A variable and a
/// file of the same name do not either; they live in different copies.
/// </summary>
public static class SessionSecretConflicts
{
    public sealed record Conflict(string Delivery, string Name, IReadOnlyList<string> Ids);

    public static Conflict? Find(IEnumerable<SessionSecretStore.Entry> entries) =>
        entries.DistinctBy(e => e.Id)
            .GroupBy(e => (e.Delivery, e.Name))
            .Where(g => g.Count() > 1)
            .Select(g => new Conflict(g.Key.Delivery, g.Key.Name, g.Select(e => e.Id).ToList()))
            .FirstOrDefault();

    /// <summary>Where the colliding secrets would land, as the agent would look for them.</summary>
    public static string Place(Conflict c) => c.Delivery == SessionSecretDelivery.Env
        ? $"the environment variable {c.Name}"
        : $"the file ~/.secrets/{c.Name}";

    /// <summary>Throws when <paramref name="entries"/> hold two secrets landing in one place.
    /// <paramref name="subject"/> starts the sentence: "This session would receive".</summary>
    public static void EnsureNone(IEnumerable<SessionSecretStore.Entry> entries, string subject)
    {
        if (Find(entries) is not { } c) return;
        throw new SessionSecretConflictException(
            $"{subject} {c.Ids.Count} secrets as {Place(c)} (ids {string.Join(", ", c.Ids)}). "
            + "A session can receive only one value there: leave all but one of them out, or rename one.");
    }

    /// <summary>
    /// Checks every project selection and every session's effective set against
    /// <paramref name="stored"/>, as they would be after a change the caller already applied to
    /// the arguments. <paramref name="onlySessions"/> and <paramref name="onlyProjects"/> limit the
    /// check to what the change can affect; null checks all of them.
    /// </summary>
    public static void EnsureNone(IReadOnlyList<SessionSecretStore.Entry> stored,
        IReadOnlyDictionary<string, SessionRecord> sessions,
        IReadOnlyDictionary<string, (string Name, IReadOnlyList<string> SecretIds)> projects,
        IReadOnlyCollection<string>? onlySessions = null, IReadOnlyCollection<string>? onlyProjects = null,
        string? selfSessionId = null)
    {
        var byId = stored.ToDictionary(e => e.Id);
        IEnumerable<SessionSecretStore.Entry> Entries(IEnumerable<string> ids) =>
            ids.Select(id => byId.GetValueOrDefault(id)).OfType<SessionSecretStore.Entry>();

        // A project selecting both would hand both to every session that follows it, including
        // the ones created later, so it is refused even while no session is in it.
        foreach (var project in projects.Where(p => onlyProjects is null || onlyProjects.Contains(p.Key)).Select(p => p.Value))
            EnsureNone(Entries(project.SecretIds), $"The project “{project.Name}” would select");

        var resolver = new SessionSecretAccessResolver(sessions,
            projects.ToDictionary(p => p.Key, p => p.Value.SecretIds), byId.Keys.ToList());
        foreach (var session in sessions.Values.Where(s => onlySessions is null || onlySessions.Contains(s.Id)))
        {
            EnsureNone(Entries(resolver.Effective(session)), session.Id == selfSessionId
                ? "This session would receive"
                : $"The session “{(string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title)}” ({session.Id}) would receive");
        }
    }
}
