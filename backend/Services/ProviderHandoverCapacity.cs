using System.Text;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public sealed record HandoverCapacity(long WindowTokens, int InputUpperBound, int SafetyMarginTokens, string Source);

public static class ProviderHandoverCapacity
{
    public const int NativeMinimumTokens = 100_000;
    public const int SafetyMarginTokens = 32_768;
    public static HandoverCapacity Check(AgentKind agent, AgentAuthMode authMode, string text, long? reportedWindow)
    {
        if (agent is not (AgentKind.Claude or AgentKind.Codex)) throw new ArgumentException("Unsupported target provider.");
        var native = authMode is AgentAuthMode.Subscription or AgentAuthMode.ApiKey;
        var window = reportedWindow is > 0 ? reportedWindow.Value : native ? NativeMinimumTokens : 0;
        // A byte bound is deliberately conservative for multilingual selections; a four-characters
        // estimate would undercount some scripts. The reserve includes native framing and output.
        var upperBound = Encoding.UTF8.GetByteCount(text);
        if (window == 0) throw new InvalidOperationException("The custom endpoint has not reported a context window.");
        if (window < (long)upperBound + SafetyMarginTokens)
            throw new InvalidOperationException("The target context window is too small for this handover and its safety margin.");
        return new(window, upperBound, SafetyMarginTokens, reportedWindow is > 0 ? "reported" : "native-minimum");
    }
}
