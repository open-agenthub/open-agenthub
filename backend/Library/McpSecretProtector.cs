using Microsoft.AspNetCore.DataProtection;

namespace AgentHub.Api.Library;

/// <summary>
/// Encrypts MCP catalog <c>secret_json</c> at rest via ASP.NET Data Protection.
/// </summary>
public interface IMcpSecretProtector
{
    /// <summary>Protect plaintext for storage. Null/empty → null (no secret).</summary>
    string? Protect(string? plaintext);

    /// <summary>Unprotect a stored payload. Null/empty → null.</summary>
    string? Unprotect(string? protectedPayload);
}

public sealed class McpSecretProtector : IMcpSecretProtector
{
    public const string Purpose = "mcp-server-secrets";

    private readonly IDataProtector _protector;

    public McpSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    public string? Unprotect(string? protectedPayload) =>
        string.IsNullOrEmpty(protectedPayload) ? null : _protector.Unprotect(protectedPayload);
}
