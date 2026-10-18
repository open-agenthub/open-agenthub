using System.Net;
using System.Text;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public interface IWorkspaceDiffReader
{
    Task<string?> ReadAsync(SessionRecord session, string comparison, CancellationToken ct);
}

public sealed class WorkspaceDiffReader(
    HttpClient http, ISessionService sessions, IConfiguration configuration) : IWorkspaceDiffReader
{
    public async Task<string?> ReadAsync(SessionRecord session, string comparison, CancellationToken ct)
    {
        var live = await sessions.GetSessionAsync(session.Owner, session.Id, ct);
        if (live?.Phase != "Running" || string.IsNullOrEmpty(live.PodIp)) return null;
        var uri = new UriBuilder("http", live.PodIp,
            configuration.GetValue("AgentHub:AgentPort", 7681), "agenthub/diff")
        {
            Query = "comparison=" + Uri.EscapeDataString(comparison)
        }.Uri;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-Agent-Token", session.CallbackToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
            // Bound an older or faulty pod too, rather than trusting its Content-Length header.
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (body.Length + read > 2 * 1024 * 1024) return null;
                body.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(body.ToArray());
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
