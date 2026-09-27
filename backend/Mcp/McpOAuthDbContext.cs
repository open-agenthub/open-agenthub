using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AgentHub.Api.Mcp;

/// <summary>
/// EF Core context backing the OpenIddict authorization server. It uses the in-memory
/// provider: the only state worth keeping across restarts is the set of registered clients,
/// and those live in Postgres (<see cref="McpClientStore"/>) and are replayed into this store
/// at startup. Access tokens are self-contained, so nothing else needs persisting.
/// </summary>
public sealed class McpOAuthDbContext(DbContextOptions<McpOAuthDbContext> options) : DbContext(options);

/// <summary>Builds the OpenIddict application descriptor for a dynamically registered MCP client.</summary>
public static class McpClientDescriptor
{
    public static OpenIddictApplicationDescriptor Build(
        string clientId, string? displayName, IEnumerable<string> redirectUris, McpOAuthOptions options)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            // Consent is enforced by McpAuthorizationController against McpClientStore, not by
            // OpenIddict's authorization store — that store is unavailable because token storage
            // is disabled. Switching this to Explicit makes OpenIddict look for an authorization
            // entry that nothing ever creates, and every flow fails.
            ConsentType = ConsentTypes.Implicit,
            DisplayName = displayName ?? "MCP client",
            // Fully qualified: the project has its own AgentHub.Api.Permissions namespace, which
            // otherwise wins over OpenIddictConstants.Permissions here.
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Prefixes.Scope + Scopes.OpenId,
                OpenIddictConstants.Permissions.Prefixes.Scope + Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + Scopes.Email,
                OpenIddictConstants.Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                OpenIddictConstants.Permissions.Prefixes.Scope + McpScopes.Mcp,
                OpenIddictConstants.Permissions.Prefixes.Resource + options.McpResource
            },
            Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange }
        };

        var parsed = redirectUris
            .Select(uri => Uri.TryCreate(uri, UriKind.Absolute, out var value) ? value : null)
            .Where(uri => uri is not null);
        foreach (var uri in parsed) descriptor.RedirectUris.Add(uri!);

        return descriptor;
    }
}

public static class McpScopes
{
    public const string Mcp = "mcp";
}
