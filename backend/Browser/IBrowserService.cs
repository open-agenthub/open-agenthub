using System.Net;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Browser;

public interface IBrowserService
{
    Task<BrowserConnection> EnsureAsync(SessionRecord session, IPAddress agentPodIp, CancellationToken ct = default);
    Task<BrowserSummary> GetSummaryAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(
        IReadOnlyCollection<string> sessionIds, CancellationToken ct = default);
    Task<BrowserConnection?> GetConnectionAsync(string sessionId, CancellationToken ct = default);
    Task StopAsync(string sessionId, CancellationToken ct = default);
    Task<BrowserStateUrls?> MintStateUrlsAsync(
        string leaseId, string token, CancellationToken ct = default);
    Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default);
}