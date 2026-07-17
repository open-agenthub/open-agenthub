using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Ee.Sharing;

public enum ShareRole
{
    Viewer,
    Collaborator
}

public enum SessionAccessLevel
{
    None,
    Viewer,
    Collaborator,
    Owner
}

public sealed record SessionAccessResult(
    SessionRecord Session,
    SessionAccessLevel Level,
    string? SharedBy);

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

    public static bool CanWriteTerminal(SessionAccessLevel level)
        => level is SessionAccessLevel.Collaborator or SessionAccessLevel.Owner;
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

public sealed record SessionSharingOverview(
    IReadOnlyList<DirectSessionShare> Users,
    IReadOnlyList<SessionShareLink> Links,
    SessionMcpPolicy? McpPolicy);

public sealed record CreateUserShareRequest(string Recipient, ShareRole Role);
public sealed record UpdateShareRoleRequest(ShareRole Role);
public sealed record CreateShareLinkRequest(ShareRole Role, DateTime? ExpiresAt);
public sealed record UpdateShareLinkRequest(ShareRole Role, DateTime? ExpiresAt);
public sealed record UpdateMcpPolicyRequest(
    IReadOnlyList<string> BlockedServers,
    IReadOnlyList<string> BlockedTools);
public sealed record CreatedShareLinkResponse(SessionShareLink Link, string Url);

public sealed record SharedSessionInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Owner { get; init; }
    public required SessionMode Mode { get; init; }
    public required string Phase { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Schedule { get; init; }
    public bool QuestionPending { get; init; }
    public IReadOnlyList<string> RepositoryUrls { get; init; } = [];
    public required string AccessRole { get; init; }
    public string? SharedBy { get; init; }
    public bool CanWrite { get; init; }
    public bool CanManage { get; init; }
    public bool CanShell { get; init; }
    public string? McpConfigJson { get; init; }
}

public static class SharedSessionSanitizer
{
    public static SharedSessionInfo Sanitize(SessionAccessResult access)
    {
        var session = access.Session;
        return new SharedSessionInfo
        {
            Id = session.Id,
            Title = session.Title,
            Owner = session.Owner,
            Mode = session.Mode,
            Phase = session.Status,
            CreatedAt = session.CreatedAt,
            Schedule = session.Schedule,
            QuestionPending = session.QuestionPending,
            RepositoryUrls = RepositoryUrls(session),
            AccessRole = access.Level.ToString(),
            SharedBy = access.SharedBy,
            CanWrite = SessionAccessRules.CanWriteTerminal(access.Level),
            CanManage = access.Level == SessionAccessLevel.Owner,
            CanShell = access.Level == SessionAccessLevel.Owner,
            McpConfigJson = null
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
                            urls.Add(url.GetString()!);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Legacy or malformed repository metadata is omitted from shared responses.
            }
        }

        if (!string.IsNullOrWhiteSpace(session.RepoUrl))
            urls.Add(session.RepoUrl);

        return urls.Distinct(StringComparer.Ordinal).ToArray();
    }
}
