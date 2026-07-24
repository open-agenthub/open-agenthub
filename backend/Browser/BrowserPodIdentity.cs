using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Api.Browser;

public static class BrowserPodIdentity
{
    public const string Label = "agenthub.dev/browser-identity";

    public static string FromToken(string callbackToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callbackToken)))[..32]
            .ToLowerInvariant();
}
