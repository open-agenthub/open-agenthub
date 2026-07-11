using AgentHub.Api.Persistence;

namespace AgentHub.Api.Notifications;

public interface INotifier
{
    Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default);
}

/// <summary>Sends a compact JSON payload to an n8n webhook. n8n handles the routing (push, Slack, …).</summary>
public sealed class N8nNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly string? _webhookUrl;
    private readonly string _frontendOrigin;
    private readonly ILogger<N8nNotifier> _log;

    public N8nNotifier(HttpClient http, IConfiguration cfg, ILogger<N8nNotifier> log)
    {
        _http = http;
        _webhookUrl = cfg["N8n:WebhookUrl"];
        _frontendOrigin = cfg["FrontendOrigin"] ?? "http://localhost:5173";
        _log = log;
    }

    public async Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            _log.LogDebug("No n8n webhook configured – skipping notification.");
            return;
        }

        var payload = new
        {
            @event = eventType,           // question | finished | failed
            sessionId = s.Id,
            title = s.Title,
            owner = s.Owner,
            mode = s.Mode.ToString(),
            message,
            url = $"{_frontendOrigin}/?session={s.Id}",
            timestamp = DateTime.UtcNow
        };

        try { await _http.PostAsJsonAsync(_webhookUrl, payload, ct); }
        catch (Exception e) { _log.LogWarning(e, "n8n notification failed"); }
    }
}
