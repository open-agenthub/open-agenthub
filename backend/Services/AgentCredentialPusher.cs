using System.Net.Http.Headers;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>Hands a provider credential file to a running session pod, which installs it and
/// restarts its agent with resume (docs/provider-accounts.md, "Switching the account").</summary>
public interface IAgentCredentialPusher
{
    Task PushAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, CancellationToken ct);
}

public sealed class AgentCredentialPusher : IAgentCredentialPusher
{
    public const string ProviderHeader = "X-Agent-Provider";

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

    public async Task PushAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, CancellationToken ct)
    {
        var uri = new UriBuilder("http", podIp, _agentPort, "agenthub/credentials").Uri;
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.Add("X-Agent-Token", callbackToken);
        request.Headers.Add(ProviderHeader, agent.ToString().ToLowerInvariant());
        request.Content = new ByteArrayContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The session pod did not accept the credential (HTTP {(int)response.StatusCode}).",
                null, response.StatusCode);
    }
}
