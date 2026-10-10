using System.Net.Http.Headers;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>Hands a provider credential file to a running session pod, which installs it and
/// restarts its agent with resume (docs/provider-accounts.md, "Switching the account").</summary>
public interface IAgentCredentialPusher
{
    Task PushAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, CancellationToken ct)
        => PushAsync(podIp, callbackToken, agent, file, reason: null, ct);

    /// <summary>With <paramref name="reason"/>, the pod shows that text in its restart line and
    /// as an <c>account-switched</c> event instead of the generic "account switched"
    /// (docs/account-limits.md). Null keeps the generic line.</summary>
    Task PushAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, string? reason, CancellationToken ct);
}

public sealed class AgentCredentialPusher : IAgentCredentialPusher
{
    public const string ProviderHeader = "X-Agent-Provider";
    public const string ReasonHeader = "X-Agent-Switch-Reason";

    private readonly HttpClient _http;
    private readonly int _agentPort;

    public AgentCredentialPusher(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _agentPort = configuration.GetValue("AgentHub:AgentPort", 7681);
        // A pod that is mid-restart or wedged must not hold the user's request open; the agent
        // answers as soon as the file is on disk, before it restarts anything.
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public Task PushAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, CancellationToken ct)
        => PushAsync(podIp, callbackToken, agent, file, reason: null, ct);

    public async Task PushAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, string? reason, CancellationToken ct)
    {
        var uri = new UriBuilder("http", podIp, _agentPort, "agenthub/credentials").Uri;
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.Add("X-Agent-Token", callbackToken);
        request.Headers.Add(ProviderHeader, agent.ToString().ToLowerInvariant());
        // A header carries only Latin-1 safely; the labels in a reason can be anything, so the
        // text goes out percent-encoded and the pod decodes it.
        if (!string.IsNullOrWhiteSpace(reason)) request.Headers.Add(ReasonHeader, Uri.EscapeDataString(reason.Trim()));
        request.Content = new ByteArrayContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The session pod did not accept the credential (HTTP {(int)response.StatusCode}).",
                null, response.StatusCode);
    }
}
