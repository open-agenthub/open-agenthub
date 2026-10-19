using System.Net;
using AgentHub.Api.Browser;

namespace AgentHub.Api.Services;

/// <summary>
/// The one way a request carrying a session's callback token or credentials reaches its pod
/// (#177, docs/transcripts.md "Which pod gets the token"). The address in the session's status
/// can outlive the pod: a deleted pod's IP is handed to the next pod scheduled on the node, which
/// may be another owner's session. Every send therefore asks the Kubernetes API, right before
/// it, whether that address is still this session's live agent pod, and nothing leaves when it
/// is not.
/// </summary>
public interface IAgentPodRequests
{
    /// <summary>
    /// Sends <paramref name="request"/>, whose URI must name the pod by IP address, once the
    /// address is confirmed as <paramref name="sessionId"/>'s live agent pod. Throws
    /// <see cref="AgentPodNotConfirmedException"/> without sending anything otherwise.
    /// <paramref name="timeout"/> bounds the send the way <see cref="HttpClient.Timeout"/> does:
    /// up to the headers with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, the whole
    /// body otherwise.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(string sessionId, HttpRequestMessage request,
        HttpCompletionOption completion, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// The same confirmation, for a connection that is not an HTTP request through this helper:
    /// the terminal WebSocket the browser proxy and the chat relays open (#229). Throws
    /// <see cref="AgentPodNotConfirmedException"/> unless <paramref name="podIp"/> is
    /// <paramref name="sessionId"/>'s live agent pod; connect only after it returns.
    /// </summary>
    Task ConfirmAsync(string sessionId, string? podIp, CancellationToken ct);
}

/// <summary>
/// The address is not (or could not be confirmed as) the session's live pod; nothing was sent.
/// An <see cref="HttpRequestException"/>, so every caller treats it as the unreachable pod it
/// effectively is: the reader shows nothing live, the pusher reports the pod did not take it.
/// </summary>
public sealed class AgentPodNotConfirmedException(string message, Exception? inner = null)
    : HttpRequestException(message, inner);

public sealed class AgentPodRequests : IAgentPodRequests
{
    // The confirmation is one list call against the API server; a wedged API server must not
    // hold the caller's request for its whole timeout.
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly IAgentPodIdentityResolver _pods;

    /// <summary>Sends with <paramref name="http"/> once <paramref name="pods"/> confirms the address.</summary>
    /// <param name="http">Must not follow redirects: a pod answering 307 to another address would
    /// otherwise get the token sent on to it, past the check. <see cref="AddAgentPodRequests"/>
    /// registers it that way.</param>
    public AgentPodRequests(HttpClient http, IAgentPodIdentityResolver pods)
    {
        _http = http;
        _pods = pods;
        // Each call brings its own bound; the client's own would cut the longest of them short.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<HttpResponseMessage> SendAsync(string sessionId, HttpRequestMessage request,
        HttpCompletionOption completion, TimeSpan timeout, CancellationToken ct)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
            throw new AgentPodNotConfirmedException("The pod address is not an IP address.");
        await ConfirmAsync(sessionId, uri.IdnHost, ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        return await _http.SendAsync(request, completion, deadline.Token);
    }

    public async Task ConfirmAsync(string sessionId, string? podIp, CancellationToken ct)
    {
        if (!IPAddress.TryParse(podIp, out var address))
            throw new AgentPodNotConfirmedException("The pod address is not an IP address.");
        bool live;
        using (var confirmation = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            confirmation.CancelAfter(ConfirmTimeout);
            try
            {
                live = await _pods.IsLiveSessionPodAsync(sessionId, address, confirmation.Token);
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                error is OperationCanceledException or HttpRequestException or k8s.Autorest.HttpOperationException)
            {
                throw new AgentPodNotConfirmedException("The session pod could not be confirmed.", error);
            }
        }
        if (!live) throw new AgentPodNotConfirmedException("The address no longer belongs to the session's pod.");
    }
}

public static class AgentPodRequestsRegistration
{
    /// <summary>The helper with a client that never follows a redirect.</summary>
    public static IServiceCollection AddAgentPodRequests(this IServiceCollection services)
    {
        services.AddHttpClient<IAgentPodRequests, AgentPodRequests>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
