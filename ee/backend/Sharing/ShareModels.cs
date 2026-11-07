// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Api.Models;
using AgentHub.Api.Browser;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Ee.Sharing;

public enum ShareRole
{
    Viewer,
    Collaborator
}

public sealed class ShareRoleJsonConverter : JsonConverter<ShareRole>
{
    public override ShareRole Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Share role must be a string.");

        var value = reader.GetString();
        if (string.Equals(value, nameof(ShareRole.Viewer), StringComparison.OrdinalIgnoreCase))
            return ShareRole.Viewer;
        if (string.Equals(value, nameof(ShareRole.Collaborator), StringComparison.OrdinalIgnoreCase))
            return ShareRole.Collaborator;

        throw new JsonException("Share role must be Viewer or Collaborator.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ShareRole value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

public enum SessionAccessLevel
{
    None,
    Viewer,
    Collaborator,
    Owner
}

/// <summary>What a principal may do with a session, and on whose behalf.</summary>
/// <param name="InheritedFrom">The ancestor whose share — or whose project's share — grants this
/// access, when no grant on the session itself is at least as strong; null for the owner, a direct
/// share, a link and a share on the session's own project.</param>
/// <param name="SharedViaProject">The name of the project whose share grants this access (#409);
/// null when a share on a session does.</param>
public sealed record SessionAccessResult(
    SessionRecord Session,
    SessionAccessLevel Level,
    string? SharedBy,
    string? InheritedFrom = null,
    string? SharedViaProject = null);

/// <summary>What a grant is attached to. Ordered from the most to the least specific: when two
/// grants give the same role, the more specific one is reported as the reason.</summary>
public enum ShareGrantScope
{
    /// <summary>A direct share or a link on the session itself.</summary>
    Session,
    /// <summary>A direct share on a session up the parentSessionId chain (#410).</summary>
    Ancestor,
    /// <summary>A share on the project of the session or of one of its ancestors (#409). Least
    /// specific: it names no session at all.</summary>
    Project
}

/// <summary>One grant that could open a session for a principal; see <see cref="SessionAccessRules.Decide"/>.</summary>
/// <param name="SessionId">The session the grant is stored on: the session itself, or the
/// ancestor it is inherited from.</param>
/// <param name="Depth">Steps up the chain to <paramref name="SessionId"/>; 0 on the session.</param>
/// <param name="ProjectId">For <see cref="ShareGrantScope.Project"/>: the project the grant is
/// stored on, which is the project of <paramref name="SessionId"/>.</param>
/// <param name="ProjectName">That project's name, so a recipient can be told where access comes from.</param>
public sealed record ShareGrant(
    ShareRole Role,
    ShareGrantScope Scope,
    string SessionId,
    int Depth = 0,
    string? ProjectId = null,
    string? ProjectName = null);

public sealed record SessionAccessDecision(SessionAccessLevel Level, ShareGrant? Via);

/// <summary>
/// The one place that turns grants into an access level. Every surface — the web API, the
/// terminal and file proxies, the token API and the MCP tools — reaches it through
/// <see cref="SessionAccessService"/> or <see cref="SessionSharingService"/>; the store only
/// collects the candidate grants. Precedence is documented in <c>docs/session-sharing.md</c>.
/// </summary>
public static class SessionAccessRules
{
    public static SessionAccessLevel Resolve(bool owns, ShareRole? role) => owns
        ? SessionAccessLevel.Owner
        : role switch
        {
            ShareRole.Collaborator => SessionAccessLevel.Collaborator,
            ShareRole.Viewer => SessionAccessLevel.Viewer,
            _ => SessionAccessLevel.None
        };

    /// <summary>
    /// Ownership wins outright. Otherwise the strongest role among all grants applies, wherever it
    /// is attached: a child shared as Viewer under a parent shared as Collaborator is opened as
    /// Collaborator, and a child shared as Collaborator under a Viewer parent likewise. A grant
    /// can widen access but never narrow it, so no grant anywhere has to be checked for a
    /// "deny" — revoking is removing a grant. Among equally strong grants the most specific
    /// (own session before an ancestor, an ancestor before a project, the nearer one first) is
    /// reported as <c>Via</c>. A project grant (#409) is one more candidate in the same contest,
    /// so an explicit session share can add to a project's role but never take it away.
    /// </summary>
    public static SessionAccessDecision Decide(bool owns, IEnumerable<ShareGrant> grants)
    {
        if (owns)
            return new SessionAccessDecision(SessionAccessLevel.Owner, null);

        ShareGrant? best = null;
        var bestLevel = SessionAccessLevel.None;
        foreach (var grant in grants
                     .OrderBy(grant => grant.Scope)
                     .ThenBy(grant => grant.Depth))
        {
            var level = Resolve(false, grant.Role);
            if (level > bestLevel)
            {
                best = grant;
                bestLevel = level;
            }
        }
        return new SessionAccessDecision(bestLevel, best);
    }

    /// <summary>The access result for a non-owner, or null when no grant opens the session.</summary>
    public static SessionAccessResult? ForRecipient(SessionRecord session, IEnumerable<ShareGrant> grants)
    {
        var decision = Decide(false, grants);
        if (decision.Level == SessionAccessLevel.None)
            return null;
        var via = decision.Via!;
        // A project grant found on an ancestor reaches this session through that ancestor, as a
        // session share on it would; one found on the session itself is inherited from nobody.
        var inheritedFrom = via.Scope != ShareGrantScope.Session && via.Depth > 0 ? via.SessionId : null;
        var project = via.Scope == ShareGrantScope.Project ? via.ProjectName : null;
        return new SessionAccessResult(session, decision.Level, session.Owner, inheritedFrom, project);
    }

    public static bool CanWriteTerminal(SessionAccessLevel level)
        => level is SessionAccessLevel.Collaborator or SessionAccessLevel.Owner;

    public static bool CanReadFiles(SessionAccessLevel level)
        => level is SessionAccessLevel.Viewer or SessionAccessLevel.Collaborator
            or SessionAccessLevel.Owner;

    public static bool CanWriteFiles(SessionAccessLevel level)
        => level is SessionAccessLevel.Collaborator or SessionAccessLevel.Owner;

    public static bool CanManageFiles(SessionAccessLevel level)
        => level == SessionAccessLevel.Owner;
}

public sealed record IssuedShareToken(string Token, byte[] Hash);

public static class ShareTokens
{
    private const int TokenBytes = 32;
    private const int HashBytes = 32;
    private const int EncodedTokenLength = 43;

    public static IssuedShareToken Issue()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return new IssuedShareToken(token, SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];

        if (token is null || token.Length != EncodedTokenLength)
            return false;

        foreach (var character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                return false;
        }

        try
        {
            var encoded = token.Replace('-', '+').Replace('_', '/') + "=";
            if (Convert.FromBase64String(encoded).Length != TokenBytes)
                return false;
        }
        catch (FormatException)
        {
            return false;
        }

        hash = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        return true;
    }

    public static bool Matches(string? token, ReadOnlySpan<byte> storedHash)
    {
        if (storedHash.Length != HashBytes || !TryHash(token, out var candidateHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(candidateHash, storedHash);
    }
}

public sealed record DirectSessionShare(
    string Recipient,
    ShareRole Role,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SessionShareLink(
    string Id,
    ShareRole Role,
    DateTime? ExpiresAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastUsedAt);

public sealed record IssuedSessionShareLink(SessionShareLink Link, string Token);

public sealed record SessionMcpPolicy(
    IReadOnlyList<string> BlockedServers,
    IReadOnlyList<string> BlockedTools,
    DateTime UpdatedAt);

public interface ISessionMcpPolicyReader
{
    Task<SessionMcpPolicy?> GetMcpPolicyAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Whether anybody besides the owner can currently open a session. Its own interface because the
/// one caller outside sharing — project file access — must not be able to do anything else with
/// the share tables.
/// </summary>
public interface ISessionShareStatus
{
    Task<bool> IsSharedAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>What the owner's sharing dialog shows for one session.</summary>
/// <param name="ProjectShares">The shares on the session's project (#409). They open the session
/// as well, and a share on the session can only add to them, so the dialog lists them beside its
/// own.</param>
public sealed record SessionSharingOverview(
    IReadOnlyList<DirectSessionShare> Users,
    IReadOnlyList<SessionShareLink> Links,
    SessionMcpPolicy? McpPolicy,
    IReadOnlyList<ProjectShare>? ProjectShares = null);

/// <summary>Who a project share is for: one signed-in user, or every member of an IdP group.</summary>
public enum ProjectShareSubjectType
{
    User,
    Group
}

public sealed class ProjectShareSubjectTypeJsonConverter : JsonConverter<ProjectShareSubjectType>
{
    public override ProjectShareSubjectType Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && ProjectShareSubjects.TryParse(reader.GetString(), out var type))
        {
            return type;
        }
        throw new JsonException("Subject type must be user or group.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ProjectShareSubjectType value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(ProjectShareSubjects.Format(value));
}

public static class ProjectShareSubjects
{
    /// <summary>The spelling stored in <c>project_shares.subject_type</c> and used on the wire,
    /// the same as <c>library_shares</c> uses for its subjects.</summary>
    public static string Format(ProjectShareSubjectType type) => type switch
    {
        ProjectShareSubjectType.User => "user",
        ProjectShareSubjectType.Group => "group",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    public static bool TryParse(string? value, out ProjectShareSubjectType type)
    {
        if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase))
        {
            type = ProjectShareSubjectType.User;
            return true;
        }
        if (string.Equals(value, "group", StringComparison.OrdinalIgnoreCase))
        {
            type = ProjectShareSubjectType.Group;
            return true;
        }
        type = default;
        return false;
    }
}

/// <summary>One share on a project: every session in it, existing and future, opens for the
/// subject with this role.</summary>
public sealed record ProjectShare(
    [property: JsonConverter(typeof(ProjectShareSubjectTypeJsonConverter))] ProjectShareSubjectType SubjectType,
    string Subject,
    [property: JsonConverter(typeof(ShareRoleJsonConverter))] ShareRole Role,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UpsertProjectShareRequest(
    [property: JsonConverter(typeof(ProjectShareSubjectTypeJsonConverter))] ProjectShareSubjectType SubjectType,
    string Subject,
    [property: JsonConverter(typeof(ShareRoleJsonConverter))] ShareRole Role);

public sealed record CreateUserShareRequest(
    string Recipient,
    [property: JsonConverter(typeof(ShareRoleJsonConverter))] ShareRole Role);
public sealed record UpdateShareRoleRequest(
    [property: JsonConverter(typeof(ShareRoleJsonConverter))] ShareRole Role);
public sealed record CreateShareLinkRequest(
    [property: JsonConverter(typeof(ShareRoleJsonConverter))] ShareRole Role,
    DateTime? ExpiresAt);
public sealed record UpdateShareLinkRequest(
    [property: JsonConverter(typeof(ShareRoleJsonConverter))] ShareRole Role,
    DateTime? ExpiresAt);
public sealed record UpdateMcpPolicyRequest(
    IReadOnlyList<string> BlockedServers,
    IReadOnlyList<string> BlockedTools);
public sealed record CreatedShareLinkResponse(SessionShareLink Link, string Url);

public sealed record SharedSessionInfo
{
    public AgentKind Agent { get; init; }
    public long ExecutionGeneration { get; init; }
    public bool ProviderSwitchPending { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Owner { get; init; }
    public required SessionMode Mode { get; init; }
    public string UiMode { get; init; } = SessionUiMode.Terminal;
    public required string Phase { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Schedule { get; init; }
    public bool QuestionPending { get; init; }
    public bool QuestionOpen { get; init; }
    public string? QuestionTitle { get; init; }
    public IReadOnlyList<string> RepositoryUrls { get; init; } = [];
    public required string AccessRole { get; init; }
    public string? SharedBy { get; init; }
    /// <summary>Set only when the recipient can open the parent as well, so a child is nested
    /// under it in the recipient's list without naming a session they cannot see.</summary>
    public string? ParentSessionId { get; init; }
    /// <summary>The ancestor whose share opens this session (#410); null for a direct share.</summary>
    public string? InheritedFrom { get; init; }
    /// <summary>The project whose share opens this session (#409); null when a session share does.</summary>
    public string? SharedViaProject { get; init; }
    public bool CanWrite { get; init; }
    public bool CanManage { get; init; }
    public bool CanShell { get; init; }
    public string? McpConfigJson { get; init; }
    public BrowserSummary Browser { get; init; } = BrowserSummary.Stopped;
}

public static class SharedSessionSanitizer
{
    public static SharedSessionInfo Sanitize(SessionAccessResult access, BrowserSummary? browser = null)
    {
        var session = access.Session;
        return new SharedSessionInfo
        {
            Id = session.Id, Agent = session.Agent, ExecutionGeneration = session.ExecutionGeneration, ProviderSwitchPending = session.ProviderSwitchPending,
            Title = session.Title,
            Owner = session.Owner,
            Mode = session.Mode,
            UiMode = session.UiMode,
            Phase = session.Status,
            CreatedAt = session.CreatedAt,
            Schedule = session.Schedule,
            QuestionPending = session.QuestionPending,
            QuestionOpen = session.QuestionOpen,
            QuestionTitle = session.QuestionOpen ? session.QuestionTitle : null,
            RepositoryUrls = RepositoryUrls(session),
            AccessRole = access.Level.ToString(),
            SharedBy = access.SharedBy,
            // Every session between the granting ancestor and this one is opened by the same
            // grant, so for inherited access the parent is always visible to the recipient.
            ParentSessionId = access.InheritedFrom is null ? null : session.ParentSessionId,
            InheritedFrom = access.InheritedFrom,
            SharedViaProject = access.SharedViaProject,
            CanWrite = SessionAccessRules.CanWriteTerminal(access.Level),
            CanManage = access.Level == SessionAccessLevel.Owner,
            CanShell = access.Level == SessionAccessLevel.Owner,
            McpConfigJson = null,
            Browser = browser ?? BrowserSummary.Stopped
        };
    }

    private static IReadOnlyList<string> RepositoryUrls(SessionRecord session)
    {
        var urls = new List<string>();

        if (!string.IsNullOrWhiteSpace(session.ReposJson))
        {
            try
            {
                using var document = JsonDocument.Parse(session.ReposJson);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var repository in document.RootElement.EnumerateArray())
                    {
                        if (repository.ValueKind == JsonValueKind.Object
                            && repository.TryGetProperty("url", out var url)
                            && url.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(url.GetString()))
                        {
                            var sanitized = SanitizeRepositoryUrl(url.GetString());
                            if (sanitized is not null)
                                urls.Add(sanitized);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Legacy or malformed repository metadata is omitted from shared responses.
            }
        }

        if (SanitizeRepositoryUrl(session.RepoUrl) is { } legacyUrl)
            urls.Add(legacyUrl);

        return urls.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? SanitizeRepositoryUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim();
        if (candidate.Any(char.IsControl))
            return null;

        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            try
            {
                var builder = new UriBuilder(uri)
                {
                    UserName = "",
                    Password = "",
                    Query = "",
                    Fragment = ""
                };
                return builder.Uri.AbsoluteUri;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        var suffixIndex = candidate.IndexOfAny(['?', '#']);
        if (suffixIndex >= 0)
            candidate = candidate[..suffixIndex];

        var userInfoIndex = candidate.LastIndexOf('@');
        var hostStart = userInfoIndex + 1;
        int separatorIndex;

        if (hostStart < candidate.Length && candidate[hostStart] == '[')
        {
            var hostEnd = candidate.IndexOf(']', hostStart + 1);
            if (hostEnd < 0 || hostEnd + 1 >= candidate.Length || candidate[hostEnd + 1] != ':')
                return null;
            separatorIndex = hostEnd + 1;
        }
        else
        {
            separatorIndex = candidate.IndexOf(':', hostStart);
        }

        if (separatorIndex <= hostStart || separatorIndex == candidate.Length - 1)
            return null;

        var host = candidate[hostStart..separatorIndex];
        var path = candidate[(separatorIndex + 1)..];
        if (host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || host.IndexOfAny(['/', '\\', '@']) >= 0)
        {
            return null;
        }

        return $"{host}:{path}";
    }
}
