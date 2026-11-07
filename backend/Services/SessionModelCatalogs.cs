using System.Net;
using System.Text;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public interface ISessionModelCatalogs
{
    /// <summary>Files a catalog the session's pod reported under the account the session runs on.</summary>
    Task RecordAsync(SessionRecord session, ModelCatalog catalog, CancellationToken ct);

    /// <summary>
    /// Asks the running pod (<c>GET /agenthub/models</c>) and records the answer; null when the pod
    /// is not running or does not answer, e.g. an older runtime without the route.
    /// </summary>
    Task<ModelCatalog?> ProbeAsync(SessionRecord session, bool refresh, CancellationToken ct);
}

/// <summary>The backend side of model discovery (#461, docs/design/model-discovery.md).</summary>
public sealed class SessionModelCatalogs(IAgentPodRequests pods, ISessionService sessions, IConfiguration configuration,
    IModelCatalogStore store, TimeProvider? time = null) : ISessionModelCatalogs
{
    // The probe in the pod gives up after 60 s; a little more here so its own timeout answer
    // arrives instead of a dropped request.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(75);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task RecordAsync(SessionRecord session, ModelCatalog catalog, CancellationToken ct)
    {
        var key = ModelCatalogKey.ForSession(session);
        if (catalog.State == "error" && catalog.Error?.Code is not ("not_entitled" or "not_signed_in"))
        {
            // A timeout or a crashed probe says nothing about the account; it must not replace a
            // list that was right an hour ago. That one keeps its age and turns stale in time.
            var stored = await store.GetAsync(key, ct);
            if (stored?.Catalog.State is "ok" or "fallback") return;
        }
        await store.PutAsync(key, catalog, _time.GetUtcNow(), ct);
    }

    public async Task<ModelCatalog?> ProbeAsync(SessionRecord session, bool refresh, CancellationToken ct)
    {
        var live = await sessions.GetSessionAsync(session.Owner, session.Id, ct);
        if (live?.Phase != "Running" || string.IsNullOrEmpty(live.PodIp)) return null;
        var uri = new UriBuilder("http", live.PodIp, configuration.GetValue("AgentHub:AgentPort", 7681), "agenthub/models")
        {
            Query = refresh ? "refresh=1" : ""
        }.Uri;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ProbeTimeout);
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-Agent-Token", session.CallbackToken);
            using var response = await pods.SendAsync(session.Id, request, HttpCompletionOption.ResponseHeadersRead,
                ProbeTimeout, deadline.Token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (body.Length + read > ModelCatalogSanitizer.MaxBytes) return null;
                body.Write(buffer, 0, read);
            }
            json = Encoding.UTF8.GetString(body.ToArray());
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        var catalog = ModelCatalogSanitizer.Parse(json);
        if (catalog is null) return null;
        await RecordAsync(session, catalog, ct);
        return catalog;
    }
}
