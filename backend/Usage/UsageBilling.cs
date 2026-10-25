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
    /// The billing of a usage report, from hub-side state only: the session's auth mode, and for
    /// Auto — where the CLI picks between the login and the key at run time — the label the hub
    /// stored when it started the pod (<see cref="ForPod"/>, sessions.usage_billing). Nothing the
    /// pod sends is consulted: the OTLP endpoint is unauthenticated, and a value from the payload
    /// could move API-key spend out of the budget. Auto without a stored label is unknown.
    /// </summary>
    public static string Classify(string? authMode, string? storedLabel) => authMode switch
    {
        "Subscription" => Subscription,
        "ApiKey" => Api,
        "CustomEndpoint" => Custom,
        _ => IsValid(storedLabel) ? storedLabel! : Unknown
    };

    /// <summary>
    /// Who pays for a pod the hub is about to start. An unattended Claude run (<c>claude -p</c>)
    /// uses ANTHROPIC_API_KEY whenever it is set, even with a login next to it. The interactive TUI
    /// asks the person whether to use the key ("No" preselected), so with both stored the hub
    /// cannot know; with only the key, the key is the one way that session can work.
    /// </summary>
    public static string ForPod(Models.AgentAuthMode authMode, Models.SessionMode mode, bool hasApiKey, bool hasSubscription) =>
        authMode switch
        {
            Models.AgentAuthMode.Subscription => Subscription,
            Models.AgentAuthMode.ApiKey => Api,
            Models.AgentAuthMode.CustomEndpoint => Custom,
            _ when mode is Models.SessionMode.Interactive => hasApiKey && !hasSubscription ? Api : Unknown,
            _ when hasApiKey => Api,
            _ when hasSubscription => Subscription,
            _ => Unknown
        };
}
