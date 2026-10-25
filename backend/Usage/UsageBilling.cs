namespace AgentHub.Api.Usage;

/// <summary>
/// Who pays for a session's tokens, decided by the hub from what it mounted into the pod — not
/// from the cost a CLI reports. Claude Code 2.1.287 reports <c>claude_code.cost.usage</c> from its
/// own price table for a subscription login exactly as for an API key (docs/usage-tracking.md), so
/// "cost &gt; 0" says nothing about a bill; reading it that way put subscription sessions into the
/// API spend and the monthly API budget.
/// </summary>
public static class UsageBilling
{
    /// <summary>Billed against a provider API key: the reported cost is spend.</summary>
    public const string Api = "api";
    /// <summary>Covered by a subscription login: tokens and turns, no money.</summary>
    public const string Subscription = "subscription";
    /// <summary>A self-hosted model endpoint: no provider bill at all.</summary>
    public const string Custom = "custom";
    /// <summary>The CLI chose the credential itself (an interactive Auto session).</summary>
    public const string Unknown = "unknown";

    public static bool IsValid(string? value) => value is Api or Subscription or Custom or Unknown;

    /// <summary>
    /// The billing of a usage report for a session with the given auth mode. Subscription, ApiKey
    /// and CustomEndpoint are settled by the session record and a pod's own label cannot change
    /// them: the label travels in an unauthenticated OTLP payload. Only Auto — where the CLI picks
    /// between the login and the key at run time — takes the label the pod was started with
    /// (<see cref="ForPod"/>), and is <see cref="Unknown"/> without one.
    /// </summary>
    public static string Classify(string? authMode, string? podLabel) => authMode switch
    {
        "Subscription" => Subscription,
        "ApiKey" => Api,
        "CustomEndpoint" => Custom,
        _ => IsValid(podLabel) ? podLabel! : Unknown
    };

    /// <summary>
    /// The label a new pod carries. An unattended Claude run (<c>claude -p</c>) uses
    /// ANTHROPIC_API_KEY whenever it is set, even with a login next to it; the interactive TUI
    /// instead asks the person whether to use the key ("No" preselected), so the hub cannot know.
    /// </summary>
    public static string ForPod(Models.AgentAuthMode authMode, Models.SessionMode mode, bool hasApiKey, bool hasSubscription) =>
        authMode switch
        {
            Models.AgentAuthMode.Subscription => Subscription,
            Models.AgentAuthMode.ApiKey => Api,
            Models.AgentAuthMode.CustomEndpoint => Custom,
            _ when mode is Models.SessionMode.Interactive => Unknown,
            _ when hasApiKey => Api,
            _ when hasSubscription => Subscription,
            _ => Unknown
        };
}
