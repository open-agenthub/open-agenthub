namespace AgentHub.Api.Chat.Signal;

/// <summary>Configuration for the Signal integration (section "Chat:Signal"). Community feature — no license required.
/// Transport is a separate signal-cli-rest-api deployment running in json-rpc mode.</summary>
public sealed class SignalOptions
{
    public bool Enabled { get; set; }
    /// <summary>Base URL of the signal-cli-rest-api service, e.g. http://agenthub-signal-cli:8080.</summary>
    public string ApiUrl { get; set; } = "";
    /// <summary>The registered sender number (E.164, e.g. +15551234567).</summary>
    public string Number { get; set; } = "";

    public bool CanRun => Enabled && !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(Number);
}
