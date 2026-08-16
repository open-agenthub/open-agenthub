using AgentHub.Api.Models;
using AgentHub.Api.Services;

namespace AgentHub.Api.Chat;

/// <summary>Outcome of resolving "+repo" chat tokens: either a repo list or a
/// user-facing error text (never both).</summary>
public sealed record ChatRepoResolution(IReadOnlyList<RepoRef> Repos, string? Error)
{
    public static ChatRepoResolution Ok(IReadOnlyList<RepoRef> repos) => new(repos, null);
    public static ChatRepoResolution Fail(string error) => new(Array.Empty<RepoRef>(), error);
}

/// <summary>
/// Platform-neutral git-project helper for the chat integrations: lists a user's
/// projects across all connected providers and resolves "+repo" tokens from a
/// /new command into <see cref="RepoRef"/>s. All replies are plain text — the
/// platform handlers apply their own escaping. Community feature — no license
/// required.
/// </summary>
public sealed class ChatRepoService
{
    private const int MaxListed = 15;
    private const int MaxCandidatesShown = 5;

    private readonly IGitAuthService _git;
    private readonly ILogger<ChatRepoService> _log;

    public ChatRepoService(IGitAuthService git, ILogger<ChatRepoService> log)
    {
        _git = git;
        _log = log;
    }

    /// <summary>Plain-text project list for "/repos [query]", or a hint when git
    /// connect is not configured / no account is connected.</summary>
    public async Task<string> ListProjectsTextAsync(string owner, string? query, CancellationToken ct = default)
    {
        if (!_git.AnyConfigured)
            return "Git account connect is not configured on this instance.";

        var projects = await CollectProjectsAsync(owner, query, ct);
        if (projects is null)
            return "No git account connected. Connect GitHub/GitLab in AgentHub → Settings → Connected accounts.";
        if (projects.Count == 0)
            return string.IsNullOrWhiteSpace(query)
                ? "No projects found on your connected accounts."
                : $"No projects match \"{query.Trim()}\".";

        var lines = projects.Take(MaxListed)
            .Select(p => $"• {p.FullName}{(p.DefaultBranch is null ? "" : $" ({p.DefaultBranch})")} · {p.ProviderId}")
            .ToList();
        if (projects.Count > MaxListed) lines.Add($"… and {projects.Count - MaxListed} more — narrow with /repos <query>.");
        lines.Add("Start a session with a repo: /new +name <prompt> (or +group/name, +name#branch).");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Resolves "+repo" tokens to repos. A token is either a full clone URL (used
    /// verbatim) or a project name / group-name fragment matched against the connected
    /// providers' projects; "#branch" overrides the branch. Any unresolved or ambiguous
    /// token fails the whole resolution — a chat-started session must not silently run
    /// on the wrong repository.
    /// </summary>
    public async Task<ChatRepoResolution> ResolveAsync(string owner, IReadOnlyList<string> tokens, CancellationToken ct = default)
    {
        var repos = new List<RepoRef>();
        foreach (var raw in tokens)
        {
            var (reference, branch) = SplitBranch(raw);
            if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(reference, UriKind.Absolute, out _))
                    return ChatRepoResolution.Fail($"\"{reference}\" is not a valid repository URL.");
                repos.Add(new RepoRef { Url = reference, Branch = branch });
                continue;
            }

            if (!_git.AnyConfigured)
                return ChatRepoResolution.Fail("Git account connect is not configured on this instance — use a full clone URL (+https://…).");

            var projects = await CollectProjectsAsync(owner, reference, ct);
            if (projects is null)
                return ChatRepoResolution.Fail("No git account connected — connect GitHub/GitLab in AgentHub → Settings → Connected accounts, or use a full clone URL (+https://…).");

            var match = PickProject(projects, reference);
            if (match.Count == 0)
                return ChatRepoResolution.Fail($"No project matches \"{reference}\". /repos lists your projects.");
            if (match.Count > 1)
            {
                var candidates = string.Join(", ", match.Take(MaxCandidatesShown).Select(p => p.FullName));
                return ChatRepoResolution.Fail($"\"{reference}\" is ambiguous — be more specific: {candidates}");
            }

            var project = match[0];
            repos.Add(new RepoRef { Url = project.Url, Branch = branch ?? project.DefaultBranch, ProviderId = project.ProviderId });
        }
        return ChatRepoResolution.Ok(repos);
    }

    /// <summary>Projects across all connected providers (search applied per provider),
    /// or null when no provider is connected. Provider API failures degrade to that
    /// provider contributing nothing (already logged inside the git service).</summary>
    private async Task<List<GitProject>?> CollectProjectsAsync(string owner, string? query, CancellationToken ct)
    {
        var providers = await _git.ListProvidersAsync(owner, ct);
        var connected = providers.Where(p => p.Connected).ToList();
        if (connected.Count == 0) return null;

        var projects = new List<GitProject>();
        foreach (var provider in connected)
        {
            try { projects.AddRange(await _git.SearchProjectsAsync(owner, provider.Id, query, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Listing {Provider} projects for chat failed; continuing", provider.Id);
            }
        }
        return projects;
    }

    /// <summary>Narrows search results for one token: exact full-name match wins, then
    /// exact name / "…/name" suffix matches, then everything the search returned.</summary>
    private static List<GitProject> PickProject(List<GitProject> projects, string reference)
    {
        var exact = projects.Where(p => p.FullName.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        var byName = projects.Where(p =>
            p.Name.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            p.FullName.EndsWith("/" + reference, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count > 0 ? byName : projects;
    }

    private static (string Reference, string? Branch) SplitBranch(string token)
    {
        var hash = token.LastIndexOf('#');
        if (hash <= 0 || hash == token.Length - 1) return (token, null);
        return (token[..hash], token[(hash + 1)..]);
    }
}
