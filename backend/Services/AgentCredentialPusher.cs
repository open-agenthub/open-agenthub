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

    /// <summary>
    /// Hands the pod the same login with tokens the hub renewed, installed without restarting the
    /// agent (docs/provider-accounts.md, "The hub owns each login"). False when the pod cannot
    /// take it that way — an image from before the route (404) or a CLI that would need a restart
    /// (409) — which is never worth interrupting a session for.
    /// </summary>
    Task<bool> RefreshAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>
    /// Hands the pod a rebuilt git credential store and author list (docs/git-credentials.md,
    /// "Switching the token of a running session"); installed without a restart. False when the pod
    /// predates the route (404), so the caller can say "at the next start" instead of failing.
    /// </summary>
    Task<bool> PushGitCredentialsAsync(string podIp, string callbackToken, byte[] payload, CancellationToken ct)
        => Task.FromResult(false);
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
        using var request = Request(podIp, "agenthub/credentials", callbackToken, agent, file);
        // A header carries only Latin-1 safely; the labels in a reason can be anything, so the
        // text goes out percent-encoded and the pod decodes it.
        if (!string.IsNullOrWhiteSpace(reason)) request.Headers.Add(ReasonHeader, Uri.EscapeDataString(reason.Trim()));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The session pod did not accept the credential (HTTP {(int)response.StatusCode}).",
                null, response.StatusCode);
    }

    public async Task<bool> RefreshAsync(string podIp, string callbackToken, AgentKind agent, byte[] file, CancellationToken ct)
    {
        using var request = Request(podIp, "agenthub/credentials/refresh", callbackToken, agent, file);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict) return false;
        throw new HttpRequestException(
            $"The session pod did not accept the renewed credential (HTTP {(int)response.StatusCode}).",
            null, response.StatusCode);
    }

    public async Task<bool> PushGitCredentialsAsync(string podIp, string callbackToken, byte[] payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            new UriBuilder("http", podIp, _agentPort, "agenthub/git-credentials").Uri);
        request.Headers.Add("X-Agent-Token", callbackToken);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        throw new HttpRequestException(
            $"The session pod did not accept the git credentials (HTTP {(int)response.StatusCode}).",
            null, response.StatusCode);
    }

    private HttpRequestMessage Request(string podIp, string path, string callbackToken, AgentKind agent, byte[] file)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new UriBuilder("http", podIp, _agentPort, path).Uri);
        request.Headers.Add("X-Agent-Token", callbackToken);
        request.Headers.Add(ProviderHeader, agent.ToString().ToLowerInvariant());
        request.Content = new ByteArrayContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }
}
