using System.Text.RegularExpressions;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// Turns a manually stored GitHub or GitLab personal access token into git-credential-store lines.
///
/// This replaces the credential helper the pod used to install for a stored GitLab PAT. That helper
/// was registered globally, with no host attached:
///
///     git config --global --add credential.helper '!f() { echo "username=oauth2"; ... }; f'
///
/// Git only consults a helper after a 401, but that was enough: a session told to clone from a host
/// that answers 401 — any host, including one an injected prompt chose — was handed the user's
/// token. The store format binds every entry to one host, so the same credential can no longer be
/// offered to a server the user never named. It is also what the OAuth path already used, which is
/// why that path never had the problem.
///
/// Reusing the store has a second effect worth keeping: <c>setup-cli-auth.sh</c> already derives
/// <c>gh</c> and <c>glab</c> configuration from it, keyed on the user part of each line. A manual
/// PAT therefore authenticates the CLIs the same way a connected provider does, and the
/// <c>GITLAB_TOKEN</c> environment fallback that existed for that purpose is no longer needed —
/// which also takes the token out of the session's environment, where every subprocess could read
/// it.
/// </summary>
public static class ManualGitCredentials
{
    /// <summary>Hosts a legacy token stored before hosts existed migrates with; a new token always
    /// names its host.</summary>
    public const string DefaultGitLabHost = "gitlab.com";
    public const string DefaultGitHubHost = "github.com";

    /// <summary>
    /// The user part each provider kind expects. These match <c>GitProviderConfig.GitCredUser</c>
    /// deliberately: <c>setup-cli-auth.sh</c> decides whether a line configures <c>gh</c> or
    /// <c>glab</c> by looking at exactly this value, so a manual PAT has to be indistinguishable
    /// from an OAuth token of the same kind.
    /// </summary>
    public const string GitLabUser = "oauth2";
    public const string GitHubUser = "x-access-token";

    /// <summary>Longest accepted token. Well above any provider's format; a guard, not a rule.</summary>
    public const int MaxTokenLength = 1024;

    // Hostname with an optional port. No scheme, no path, no user-info: anything richer either
    // belongs in a different field or is an attempt to write a second store entry.
    private static readonly Regex HostPattern = new(
        @"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?(:[0-9]{1,5})?$", RegexOptions.Compiled);

    public static bool IsValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 259 && HostPattern.IsMatch(host);

    /// <summary>
    /// A token may not contain whitespace or control characters. A newline is the one that matters:
    /// the store is line-based, so a token containing one would append an attacker-chosen entry for
    /// an arbitrary host to the user's own credential file.
    /// </summary>
    public static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= MaxTokenLength &&
        !token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// One store line per stored PAT, in list order (see <see cref="GitPatStore"/>; legacy slots
    /// are folded in). Invalid entries are skipped rather than throwing — the values are validated
    /// when they are stored, so anything invalid here predates that validation and must not stop a
    /// session from starting.
    /// </summary>
    public static IReadOnlyList<string> Lines(IDictionary<string, byte[]>? data) => Lines(GitPatStore.Read(data));

    /// <summary>The store lines of an already-read (and possibly narrowed, see
    /// <see cref="GitPatSelection"/>) list of entries.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<GitPatStore.Entry> entries)
    {
        var lines = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (!IsValidToken(entry.Token) || !IsValidHost(entry.Host)) continue;
            var user = entry.Kind == GitPatKind.GitHub ? GitHubUser : GitLabUser;
            lines.Add($"https://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(entry.Token)}@{entry.Host}");
        }
        return lines;
    }

    /// <summary>
    /// Joins connected-provider lines and manual PAT lines into one store, or null when there are
    /// none.
    ///
    /// OAuth lines come first because git's store helper answers with the first entry matching the
    /// host. A user who both connected a provider and stored a PAT for the same host keeps the
    /// behaviour they had before: the connected provider wins, and its token is the one that gets
    /// refreshed.
    /// </summary>
    public static string? ComposeStore(string? oauthStore, IReadOnlyList<string> manualLines)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(oauthStore))
            all.AddRange(oauthStore.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var line in manualLines)
            if (!all.Contains(line)) all.Add(line);
        return all.Count == 0 ? null : string.Join("\n", all) + "\n";
    }
}
