using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AgentHub.Api.Library;

public sealed record McpGatewayTokenClaims(
    string SessionId,
    string McpServerId,
    string Owner,
    DateTimeOffset ExpiresAt);

public interface IMcpGatewayTokenService
{
    /// <summary>Issues a session-bound token for one catalog MCP server id.</summary>
    string Issue(string sessionId, string mcpServerId, string owner, TimeSpan? lifetime = null);

    /// <summary>
    /// Validates token and ensures it is bound to <paramref name="mcpServerId"/>.
    /// </summary>
    bool TryValidate(string? token, string mcpServerId, out McpGatewayTokenClaims claims);

    /// <summary>Issues a session-bound token for one ephemeral API source name.</summary>
    string IssueEphemeral(string sessionId, string name, string owner, TimeSpan? lifetime = null);

    /// <summary>
    /// Validates an ephemeral token bound to <paramref name="sessionId"/> + <paramref name="name"/>.
    /// </summary>
    bool TryValidateEphemeral(string? token, string sessionId, string name, out McpGatewayTokenClaims claims);
}

/// <summary>
/// Opaque Data-Protection tokens binding sessionId + mcpServerId (+ owner).
/// Lifetime defaults to 12h (aligned with typical spawn credential TTL).
/// Ephemeral sources use a reserved binding key <c>ephemeral:{sessionId}:{name}</c>.
/// </summary>
public sealed class McpGatewayTokenService : IMcpGatewayTokenService
{
    public const string Purpose = "mcp-gateway-tokens";
    /// <summary>Header agents send (literal value — Codex rejects literal Authorization).</summary>
    public const string HeaderName = "X-AgentHub-Mcp-Token";
    public const string EphemeralBindingPrefix = "ephemeral:";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(12);

    private readonly IDataProtector _protector;

    public McpGatewayTokenService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public static string EphemeralBindingKey(string sessionId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{EphemeralBindingPrefix}{sessionId.Trim()}:{name.Trim()}";
    }

    public string Issue(string sessionId, string mcpServerId, string owner, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpServerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var payload = new TokenPayload(
            sessionId.Trim(),
            mcpServerId.Trim(),
            owner.Trim(),
            DateTimeOffset.UtcNow.Add(lifetime ?? DefaultLifetime));
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public string IssueEphemeral(string sessionId, string name, string owner, TimeSpan? lifetime = null)
        => Issue(sessionId, EphemeralBindingKey(sessionId, name), owner, lifetime);

    public bool TryValidate(string? token, string mcpServerId, out McpGatewayTokenClaims claims)
    {
        claims = default!;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(mcpServerId))
            return false;

        // Catalog validation must not accept ephemeral binding keys.
        if (mcpServerId.Trim().StartsWith(EphemeralBindingPrefix, StringComparison.Ordinal))
            return false;

        return TryValidateCore(token, mcpServerId.Trim(), out claims);
    }

    public bool TryValidateEphemeral(string? token, string sessionId, string name, out McpGatewayTokenClaims claims)
    {
        claims = default!;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(name))
            return false;

        if (!TryValidateCore(token, EphemeralBindingKey(sessionId, name), out claims))
            return false;

        return string.Equals(claims.SessionId, sessionId.Trim(), StringComparison.Ordinal);
    }

    private bool TryValidateCore(string? token, string bindingKey, out McpGatewayTokenClaims claims)
    {
        claims = default!;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var json = _protector.Unprotect(token.Trim());
            var payload = JsonSerializer.Deserialize<TokenPayload>(json);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.SessionId)
                || string.IsNullOrWhiteSpace(payload.McpServerId)
                || string.IsNullOrWhiteSpace(payload.Owner))
                return false;

            if (!string.Equals(payload.McpServerId, bindingKey, StringComparison.Ordinal))
                return false;

            if (payload.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;

            claims = new McpGatewayTokenClaims(
                payload.SessionId, payload.McpServerId, payload.Owner, payload.ExpiresAt);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record TokenPayload(
        string SessionId,
        string McpServerId,
        string Owner,
        DateTimeOffset ExpiresAt);
}
