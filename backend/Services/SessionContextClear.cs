using System.Net;
using System.Net.Http.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public interface ISessionContextClear
{
    Task<ContextClearOutcome> ScheduleAsync(SessionInfo session, ContextClearRequest request, CancellationToken ct);
}

public sealed class SessionContextClear(IAgentPodRequests pods, ISessionStore store,
    IConfiguration configuration) : ISessionContextClear
{
    public async Task<ContextClearOutcome> ScheduleAsync(SessionInfo session, ContextClearRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FollowUp) || request.FollowUp.Length > 16_000 ||
            request.FollowUp.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("invalid_follow_up");
        if (session.Agent is not (AgentKind.Claude or AgentKind.Codex or AgentKind.OpenCode) ||
            session.Mode != SessionMode.Interactive || session.UiMode != SessionUiMode.Terminal)
            throw new ArgumentException("context_clear_unsupported");
        if (session.Phase != SessionStatus.Running || !IPAddress.TryParse(session.PodIp, out var address))
            throw new InvalidOperationException("session_not_running");
        var record = await store.GetByIdAsync(session.Id, ct);
        if (record is null) throw new InvalidOperationException("session_not_found");
        var uri = new UriBuilder("http", address.ToString(), configuration.GetValue("AgentHub:AgentPort", 7681),
            "agenthub/context-clear").Uri;
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.Add("X-Agent-Token", record.CallbackToken);
        message.Content = JsonContent.Create(request);
        using var response = await SendToLivePodAsync(session.Id, message, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("context_clear_pending");
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("context_clear_unsupported");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("context_clear_pod_refused");
        var outcome = await response.Content.ReadFromJsonAsync<ContextClearOutcome>(ct);
        return outcome is { Scheduled: true } ? outcome : throw new HttpRequestException("context_clear_not_scheduled");
    }

    // Pod IPs are reused; a context-changing request and its callback token must reach the
    // current session pod, rather than whichever pod inherited an old address.
    private async Task<HttpResponseMessage> SendToLivePodAsync(string sessionId, HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await pods.SendAsync(sessionId, message, HttpCompletionOption.ResponseContentRead,
                TimeSpan.FromSeconds(10), ct);
        }
        catch (AgentPodNotConfirmedException)
        {
            throw new InvalidOperationException("session_not_running");
        }
    }
}
