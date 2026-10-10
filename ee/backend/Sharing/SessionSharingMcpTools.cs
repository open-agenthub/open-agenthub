// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.ComponentModel;
using System.Globalization;
using AgentHub.Api.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>
/// The sharing tools of the remote MCP server. Same names and parameters as the stdio server in
/// <c>mcp/agenthub</c>; the caller is the user who approved the client (see
/// <see cref="McpCallerIdentity"/>). Deliberately a class of its own next to
/// <see cref="AgentHubMcpTools"/>: these are Enterprise features and fail with
/// <c>license_required</c> on a Community instance, while the core tools never do.
///
/// Booleans and dates arrive as strings for the reason given on <c>AgentHubMcpTools.ParseFlag</c>:
/// a connected client caches the tool schema, and a typed parameter added later fails every call
/// until it reconnects.
/// </summary>
[McpServerToolType]
public sealed class SessionSharingMcpTools(IHttpContextAccessor http, ISessionSharingService sharing)
{
    private string Owner => McpCallerIdentity.Owner(http);

    [McpServerTool(Name = "session_share")]
    [Description("Share one of your sessions with another user of this instance. Viewer (the "
                 + "default) can watch the terminal and read the transcript; Collaborator can "
                 + "also type into it. Sharing again with a different role changes the role. "
                 + "Needs an enterprise licence (license_required otherwise).")]
    public async Task<DirectSessionShare> Share(
        [Description("Session id.")] string sessionId,
        [Description("Username of the recipient, as they sign in. unknown_recipient if that "
                     + "person has never signed in here.")] string recipient,
        [Description("\"Viewer\" or \"Collaborator\". Defaults to Viewer.")] string? role = null,
        CancellationToken ct = default)
    {
        try { return await sharing.ShareWithUserAsync(Owner, sessionId, recipient, ParseRole(role), ct); }
        catch (Exception e) { throw Translate(e); }
    }

    [McpServerTool(Name = "session_unshare")]
    [Description("Revoke a user's access to one of your sessions. Links are revoked separately.")]
    public async Task<string> Unshare(
        [Description("Session id.")] string sessionId,
        [Description("Username whose access to remove.")] string recipient,
        CancellationToken ct = default)
    {
        try
        {
            await sharing.UnshareUserAsync(Owner, sessionId, recipient, ct);
            return recipient;
        }
        catch (Exception e) { throw Translate(e); }
    }

    [McpServerTool(Name = "session_share_link")]
    [Description("Create a secret link to one of your sessions. Anyone holding the link gets the "
                 + "role, so treat the url as a secret; it is returned exactly once. Revoke it "
                 + "with session_shares + the remote API, or let it expire.")]
    public async Task<ShareLinkResult> CreateLink(
        [Description("Session id.")] string sessionId,
        [Description("\"Viewer\" or \"Collaborator\". Defaults to Viewer.")] string? role = null,
        [Description("When the link stops working, as an ISO-8601 timestamp such as "
                     + "\"2026-12-31T18:00:00Z\". Omit for a link that lasts until revoked.")]
        string? expiresAt = null,
        CancellationToken ct = default)
    {
        try
        {
            var created = await sharing.CreateLinkAsync(Owner, sessionId, ParseRole(role), ParseExpiry(expiresAt), ct);
            return new ShareLinkResult(created.Link.Id, created.Url, created.Link.Role.ToString(), created.Link.ExpiresAt);
        }
        catch (Exception e) { throw Translate(e); }
    }

    [McpServerTool(Name = "session_shares")]
    [Description("Who a session of yours is shared with: direct user grants and the links that "
                 + "exist (ids and roles — never the link tokens).")]
    public async Task<SessionSharingOverview> ListShares(
        [Description("Session id.")] string sessionId,
        CancellationToken ct = default)
    {
        try { return await sharing.ListAsync(Owner, sessionId, ct); }
        catch (Exception e) { throw Translate(e); }
    }

    /// <summary>Unknown roles are reported, not defaulted: a typo must not turn a Viewer grant
    /// into a Collaborator one — or the other way round — without the caller noticing.</summary>
    private static ShareRole ParseRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ShareRole.Viewer;
        return Enum.TryParse<ShareRole>(value.Trim(), ignoreCase: true, out var role) && Enum.IsDefined(role)
            ? role
            : throw new McpException("invalid_role");
    }

    private static DateTime? ParseExpiry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // AssumeUniversal: a timestamp without an offset is read as UTC rather than as the
        // backend pod's local time, which is whatever the image's TZ happens to be.
        return DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw new McpException("invalid_expires_at");
    }

    /// <summary>The service's exceptions as the stable codes the stdio server also uses.</summary>
    private static Exception Translate(Exception e) => e switch
    {
        McpException => e,
        LicenseRequiredException => new McpException("license_required"),
        KeyNotFoundException => new McpException("session_not_found"),
        UnknownRecipientException => new McpException("unknown_recipient"),
        ArgumentException a => new McpException("invalid_request: " + a.Message),
        _ => e
    };
}

/// <summary>What <c>session_share_link</c> returns: the link's id (for revoking) and the url
/// (the one-time copy of the secret).</summary>
public sealed record ShareLinkResult(string LinkId, string Url, string Role, DateTime? ExpiresAt);
