using System.Net.Http.Json;
using System.Text;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public interface ISessionTerminalAttachments
{
    /// <summary>The pod's status and JSON body, or null when the pod is not running or does not answer.</summary>
    Task<(int Status, string Body)?> MaterializeAsync(SessionRecord session, IReadOnlyList<string> fileIds, CancellationToken ct);
}

/// <summary>
/// Hands files uploaded for the Workspace composer or pasted into the agent terminal to the pod's
/// <c>/agenthub/attachments</c> route (#148, docs/terminal-clipboard.md), which materializes them
/// and answers with the path each one is pasted into the CLI as. An older runtime has no such route
/// and answers 404, which the browser reports as "not supported by this session" instead of
/// pasting nothing.
/// </summary>
public sealed class SessionTerminalAttachments(
    HttpClient http, ISessionService sessions, IConfiguration configuration) : ISessionTerminalAttachments
{
    // Five entries of id, name, type, size and path.
    private const int MaxBytes = 64 * 1024;

    public async Task<(int Status, string Body)?> MaterializeAsync(SessionRecord session, IReadOnlyList<string> fileIds, CancellationToken ct)
    {
        var live = await sessions.GetSessionAsync(session.Owner, session.Id, ct);
        if (live?.Phase != "Running" || string.IsNullOrEmpty(live.PodIp)) return null;
        var uri = new UriBuilder("http", live.PodIp,
            configuration.GetValue("AgentHub:AgentPort", 7681), "agenthub/attachments").Uri;
        // The pod downloads an S3-backed file before it answers, with its own five-minute bound.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new { fileIds })
            };
            request.Headers.Add("X-Agent-Token", session.CallbackToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (body.Length + read > MaxBytes) return null;
                body.Write(buffer, 0, read);
            }
            return ((int)response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
