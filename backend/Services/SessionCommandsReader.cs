using System.Net;
using System.Text;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public interface ISessionCommandsReader
{
    Task<string?> ReadAsync(SessionRecord session, CancellationToken ct);
}

/// <summary>
/// The slash commands of the session's agent CLI, read from the pod's
/// <c>/agenthub/commands</c> route for the Workspace composer's completion (#169,
/// docs/slash-commands.md). Null when the pod is not running or does not answer — an older
/// runtime has no such route — and the composer then completes nothing but still sends.
/// </summary>
public sealed class SessionCommandsReader(
    HttpClient http, ISessionService sessions, IConfiguration configuration) : ISessionCommandsReader
{
    // The session agent caps its list at 400 entries of short text, well under this.
    private const int MaxBytes = 512 * 1024;

    public async Task<string?> ReadAsync(SessionRecord session, CancellationToken ct)
    {
        var live = await sessions.GetSessionAsync(session.Owner, session.Id, ct);
        if (live?.Phase != "Running" || string.IsNullOrEmpty(live.PodIp)) return null;
        var uri = new UriBuilder("http", live.PodIp,
            configuration.GetValue("AgentHub:AgentPort", 7681), "agenthub/commands").Uri;
        // Asked while a person types "/": a pod that does not answer quickly gets no completion
        // rather than holding the request open.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-Agent-Token", session.CallbackToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (body.Length + read > MaxBytes) return null;
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
