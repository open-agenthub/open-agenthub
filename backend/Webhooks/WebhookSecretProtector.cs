using Microsoft.AspNetCore.DataProtection;

namespace AgentHub.Api.Webhooks;

/// <summary>
/// Encrypts webhook trigger secrets at rest via ASP.NET Data Protection (mirrors
/// <see cref="AgentHub.Api.Library.McpSecretProtector"/>). The secret cannot be
/// hashed like an API token: GitHub's HMAC verification needs the plaintext.
/// </summary>
public interface IWebhookSecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedPayload);
}

public sealed class WebhookSecretProtector : IWebhookSecretProtector
{
    private const string Purpose = "agenthub.webhook-trigger-secret.v1";

    private readonly IDataProtector _protector;

    public WebhookSecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedPayload) => _protector.Unprotect(protectedPayload);
}
