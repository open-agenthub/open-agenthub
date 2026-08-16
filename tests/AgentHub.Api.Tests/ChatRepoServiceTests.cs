using AgentHub.Api.Chat;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class ChatRepoServiceTests
{
    private sealed class FakeGitAuthService : IGitAuthService
    {
        public bool AnyConfigured { get; set; } = true;
        public List<GitProviderInfo> Providers { get; } = new();
        public Dictionary<string, List<GitProject>> Projects { get; } = new();

        public Task<IReadOnlyList<GitProviderInfo>> ListProvidersAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitProviderInfo>>(Providers);

        public Task<IReadOnlyList<GitProject>> SearchProjectsAsync(string owner, string providerId, string? query, CancellationToken ct = default)
        {
            var all = Projects.TryGetValue(providerId, out var list) ? list : new List<GitProject>();
            var q = (query ?? "").Trim();
            return Task.FromResult<IReadOnlyList<GitProject>>(
                all.Where(p => q.Length == 0 || p.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        public string CreateAuthorizeUrl(string providerId, string owner, string redirectUri) => throw new NotSupportedException();
        public Task<string?> HandleCallbackAsync(string providerId, string code, string state, string redirectUri, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync(string owner, string providerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> BuildCredentialStoreAsync(string owner, IEnumerable<RepoRef> repos, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static GitProviderInfo Provider(string id, bool connected = true)
        => new() { Id = id, Type = id.Contains("lab") ? "gitlab" : "github", DisplayName = id, Connected = connected };

    private static GitProject Project(string fullName, string providerId, string? branch = "main")
        => new()
        {
            Name = fullName[(fullName.LastIndexOf('/') + 1)..],
            FullName = fullName,
            Url = $"https://git.example.test/{fullName}.git",
            DefaultBranch = branch,
            ProviderId = providerId
        };

    private static ChatRepoService Service(FakeGitAuthService git)
        => new(git, NullLogger<ChatRepoService>.Instance);

    // ------------------------------------------------------------------- listing

    [Fact]
    public async Task List_reports_when_git_connect_is_not_configured()
    {
        var git = new FakeGitAuthService { AnyConfigured = false };
        var text = await Service(git).ListProjectsTextAsync("alice", null);
        Assert.Contains("not configured", text);
    }

    [Fact]
    public async Task List_reports_when_no_account_is_connected()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github", connected: false));
        var text = await Service(git).ListProjectsTextAsync("alice", null);
        Assert.Contains("No git account connected", text);
    }

    [Fact]
    public async Task List_merges_connected_providers_and_shows_branch_and_provider()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github"));
        git.Providers.Add(Provider("gitlab"));
        git.Projects["github"] = new() { Project("acme/frontend", "github") };
        git.Projects["gitlab"] = new() { Project("acme/backend", "gitlab", branch: "develop") };

        var text = await Service(git).ListProjectsTextAsync("alice", null);
        Assert.Contains("acme/frontend (main) · github", text);
        Assert.Contains("acme/backend (develop) · gitlab", text);
        Assert.Contains("/new +name", text); // usage hint
    }

    [Fact]
    public async Task List_applies_the_query_and_reports_empty_results()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github"));
        git.Projects["github"] = new() { Project("acme/frontend", "github"), Project("acme/backend", "github") };

        var svc = Service(git);
        var hit = await svc.ListProjectsTextAsync("alice", "front");
        Assert.Contains("acme/frontend", hit);
        Assert.DoesNotContain("acme/backend", hit);
        Assert.Contains("No projects match \"nothing\".", await svc.ListProjectsTextAsync("alice", "nothing"));
    }

    [Fact]
    public async Task List_caps_the_output_and_mentions_the_remainder()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github"));
        git.Projects["github"] = Enumerable.Range(1, 20).Select(i => Project($"acme/repo-{i:00}", "github")).ToList();

        var text = await Service(git).ListProjectsTextAsync("alice", null);
        Assert.Contains("acme/repo-01", text);
        Assert.Contains("and 5 more", text);
        Assert.DoesNotContain("acme/repo-16", text);
    }

    // ----------------------------------------------------------------- resolving

    [Fact]
    public async Task Resolve_without_tokens_returns_an_empty_repo_list()
    {
        var git = new FakeGitAuthService();
        var res = await Service(git).ResolveAsync("alice", Array.Empty<string>());
        Assert.Null(res.Error);
        Assert.Empty(res.Repos);
    }

    [Fact]
    public async Task Resolve_accepts_full_urls_with_optional_branch()
    {
        var git = new FakeGitAuthService { AnyConfigured = false };
        var res = await Service(git).ResolveAsync("alice",
            new[] { "https://git.example.test/acme/tool.git#feature/x" });
        Assert.Null(res.Error);
        var repo = Assert.Single(res.Repos);
        Assert.Equal("https://git.example.test/acme/tool.git", repo.Url);
        Assert.Equal("feature/x", repo.Branch);
        Assert.Null(repo.ProviderId);
    }

    [Fact]
    public async Task Resolve_matches_a_unique_name_and_uses_the_default_branch()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("gitlab"));
        git.Projects["gitlab"] = new() { Project("acme/agenthub", "gitlab", branch: "develop") };

        var res = await Service(git).ResolveAsync("alice", new[] { "agenthub" });
        Assert.Null(res.Error);
        var repo = Assert.Single(res.Repos);
        Assert.Equal("https://git.example.test/acme/agenthub.git", repo.Url);
        Assert.Equal("develop", repo.Branch);
        Assert.Equal("gitlab", repo.ProviderId);
    }

    [Fact]
    public async Task Resolve_prefers_an_exact_full_name_over_substring_hits()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github"));
        git.Projects["github"] = new()
        {
            Project("acme/app", "github"),
            Project("acme/app-legacy", "github")
        };

        var res = await Service(git).ResolveAsync("alice", new[] { "acme/app" });
        Assert.Null(res.Error);
        Assert.Equal("https://git.example.test/acme/app.git", Assert.Single(res.Repos).Url);
    }

    [Fact]
    public async Task Resolve_overrides_the_branch_with_a_hash_suffix()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github"));
        git.Projects["github"] = new() { Project("acme/app", "github") };

        var res = await Service(git).ResolveAsync("alice", new[] { "app#hotfix" });
        Assert.Null(res.Error);
        Assert.Equal("hotfix", Assert.Single(res.Repos).Branch);
    }

    [Fact]
    public async Task Resolve_fails_on_unknown_and_ambiguous_tokens()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github"));
        git.Projects["github"] = new()
        {
            Project("acme/service-a", "github"),
            Project("acme/service-b", "github")
        };

        var svc = Service(git);
        var unknown = await svc.ResolveAsync("alice", new[] { "does-not-exist" });
        Assert.NotNull(unknown.Error);
        Assert.Contains("does-not-exist", unknown.Error);

        var ambiguous = await svc.ResolveAsync("alice", new[] { "service" });
        Assert.NotNull(ambiguous.Error);
        Assert.Contains("acme/service-a", ambiguous.Error);
        Assert.Contains("acme/service-b", ambiguous.Error);
    }

    [Fact]
    public async Task Resolve_fails_names_when_nothing_is_connected_but_keeps_urls_working()
    {
        var git = new FakeGitAuthService();
        git.Providers.Add(Provider("github", connected: false));

        var svc = Service(git);
        var name = await svc.ResolveAsync("alice", new[] { "app" });
        Assert.Contains("No git account connected", name.Error);

        var url = await svc.ResolveAsync("alice", new[] { "https://git.example.test/acme/app.git" });
        Assert.Null(url.Error);
        Assert.Single(url.Repos);
    }

    [Fact]
    public async Task Resolve_rejects_an_invalid_url()
    {
        var git = new FakeGitAuthService();
        var res = await Service(git).ResolveAsync("alice", new[] { "https://" });
        Assert.NotNull(res.Error);
    }
}
