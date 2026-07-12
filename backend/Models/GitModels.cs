namespace AgentHub.Api.Models;

/// <summary>
/// A configured Git OAuth provider (from configuration Git:Providers).
/// Supports github/gitlab, including self-hosted instances via BaseUrl.
/// </summary>
public sealed class GitProviderConfig
{
    /// <summary>Stable id used in URLs and stored per-repo (e.g. "github", "gitlab-corp").</summary>
    public string Id { get; set; } = "";
    /// <summary>"github" or "gitlab".</summary>
    public string Type { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Instance base URL. Default: github.com / gitlab.com.</summary>
    public string? BaseUrl { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>OAuth scopes; provider-specific defaults are used when empty.</summary>
    public string? Scopes { get; set; }

    public string Kind => Type.Trim().ToLowerInvariant();
    public string WebBase => (BaseUrl ?? (Kind == "github" ? "https://github.com" : "https://gitlab.com")).TrimEnd('/');
    public string ApiBase => Kind == "github"
        ? (BaseUrl is null ? "https://api.github.com" : $"{WebBase}/api/v3")
        : $"{WebBase}/api/v4";
    public string AuthorizeUrl => Kind == "github" ? $"{WebBase}/login/oauth/authorize" : $"{WebBase}/oauth/authorize";
    public string TokenUrl => Kind == "github" ? $"{WebBase}/login/oauth/access_token" : $"{WebBase}/oauth/token";
    public string DefaultScopes => Kind == "github" ? "repo read:user" : "api read_user";
    /// <summary>Host used in the git credential store line.</summary>
    public string GitHost => new Uri(WebBase).Host;
    /// <summary>Username part of the git credential (provider-specific).</summary>
    public string GitCredUser => Kind == "github" ? "x-access-token" : "oauth2";
}

/// <summary>Public view of a provider + whether the current user is connected.</summary>
public sealed record GitProviderInfo
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string DisplayName { get; init; }
    public required bool Connected { get; init; }
    public string? Username { get; init; }
}

/// <summary>A repository returned by the project search.</summary>
public sealed record GitProject
{
    public required string Name { get; init; }        // short name
    public required string FullName { get; init; }    // group/name
    public required string Url { get; init; }         // HTTPS clone URL
    public string? DefaultBranch { get; init; }
    public required string ProviderId { get; init; }
}
