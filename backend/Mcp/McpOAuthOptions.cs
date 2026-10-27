namespace AgentHub.Api.Mcp;

/// <summary>
/// Configuration for the remote MCP endpoint and the OAuth 2.1 authorization server that
/// guards it. <see cref="PublicBaseUrl"/> is the externally reachable HTTPS origin of this
/// deployment (e.g. https://agenthub.example.com, no trailing slash); it serves as the OAuth
/// issuer and as the base of the MCP resource identifier.
///
/// An empty value keeps the whole feature off. That is deliberate rather than defaulting to
/// the request host: the issuer ends up inside signed tokens and in discovery documents, so
/// guessing it from whatever Host header arrives would let a forwarded request mint tokens
/// for an issuer we do not control.
/// </summary>
public sealed class McpOAuthOptions
{
    public string PublicBaseUrl { get; init; } = "";

    /// <summary>
    /// Whether the instance has an OIDC authority to federate the browser login to. False means
    /// API-key mode, where the consent screen asks for the key, or the insecure development mode,
    /// where every caller is the local "dev" user and the login is skipped.
    /// </summary>
    public bool AuthEnabled { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicBaseUrl);

    public string Issuer => PublicBaseUrl.TrimEnd('/');

    /// <summary>Canonical MCP resource identifier (RFC 8707 / RFC 9728): issuer + /mcp.</summary>
    public string McpResource => Issuer + "/mcp";
}
