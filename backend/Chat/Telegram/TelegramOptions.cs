namespace AgentHub.Api.Chat.Telegram;

/// <summary>Configuration for the Telegram integration (section "Chat:Telegram"). Community feature — no license required.</summary>
public sealed class TelegramOptions
{
    /// <summary>Name of the dedicated HttpClient. It is registered with logging removed because the
    /// bot token sits in the request path — the factory's default loggers write the full URI at
    /// Information level, which would leak the token into the pod logs.</summary>
    public const string HttpClientName = "telegram";

    public bool Enabled { get; set; }
    /// <summary>Bot token from @BotFather.</summary>
    public string BotToken { get; set; } = "";

    public bool CanRun => Enabled && !string.IsNullOrWhiteSpace(BotToken);
}
