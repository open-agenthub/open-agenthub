namespace AgentHub.Api.Chat.Telegram;

/// <summary>Configuration for the Telegram integration (section "Chat:Telegram"). Community feature — no license required.</summary>
public sealed class TelegramOptions
{
    public bool Enabled { get; set; }
    /// <summary>Bot token from @BotFather.</summary>
    public string BotToken { get; set; } = "";

    public bool CanRun => Enabled && !string.IsNullOrWhiteSpace(BotToken);
}
