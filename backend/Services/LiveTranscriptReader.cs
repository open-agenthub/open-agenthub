using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// The conversation of a running session, read from the provider's transcript file in its pod
/// rather than from the copy the pod uploads every 30 seconds (#111, docs/transcripts.md).
/// </summary>
public interface ILiveTranscriptReader
{
    /// <summary>
    /// Every entry of the live transcript, or null when there is nothing live to read: the
    /// session is not running, the pod has no transcript yet, or its runtime is older than the
    /// <c>/agenthub/transcript</c> route. A null sends the caller to the stored copy.
    /// </summary>
    Task<IReadOnlyList<TranscriptEntry>?> ReadAsync(SessionInfo session, CancellationToken ct);
}

/// <summary>
/// Follows each session's transcript file by byte offset: a poll fetches only what the CLI
/// appended since the last one, parses those lines, and adds them to the entries already held.
/// The Workspace polls every second, so re-reading and re-parsing a multi-megabyte file per poll
/// (what the stored path does) would cost the pod and the hub that much per viewer per second.
/// The file is append-only for every provider the hub parses, which is what makes the offset
/// meaningful; a file that got shorter, or a different pod, starts the session over from zero.
/// </summary>
public sealed class LiveTranscriptReader : ILiveTranscriptReader
{
    // A runtime image from before the route answers 404 for it; asking again on every poll
    // would cost a request per viewer per second for an answer that cannot change until the
    // pod is replaced, which also changes its address and resets this.
    private static readonly TimeSpan UnsupportedFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleEviction = TimeSpan.FromMinutes(10);
    // Each response is at most the pod's chunk size (8 MiB); a few rounds catch a first read
    // of a long session up in one poll, and whatever is left comes with the next one.
    private const int MaxRoundsPerRead = 4;

    private const int MaxChunkBytes = 8 * 1024 * 1024;
    // Parsing also allocates: two concurrent 8 MiB readers exhausted the backend heap budget
    // in the 24-view probe. Serialize catch-up reads; docs/transcripts.md records the result.
    private readonly SemaphoreSlim _downloads = new(1, 1);
    private readonly IAgentPodIdentityResolver _pods;
    private readonly HttpClient _http;
    private readonly ISessionStore _sessions;
    private readonly int _agentPort;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, Follow> _follows = new();
    private DateTime _lastSweep;

    public LiveTranscriptReader(HttpClient http, ISessionStore sessions, IConfiguration configuration,
        IAgentPodIdentityResolver pods, Func<DateTime>? now = null)
    {
        _http = http;
        _pods = pods;
        _sessions = sessions;
        _agentPort = configuration.GetValue("AgentHub:AgentPort", 7681);
        _now = now ?? (() => DateTime.UtcNow);
        // A poll that waits on a wedged pod holds the Workspace's request; the stored copy is
        // the better answer after this long.
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    private sealed class Follow
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string? PodIp;
        public string? Token;
        public string? TranscriptId;
        public long NextByte;
        public TranscriptEntry[] Entries = [];
        public DateTime UnsupportedUntil;
        public DateTime LastUsed;

        public void Reset(string podIp)
        {
            PodIp = podIp;
            TranscriptId = null;
            NextByte = 0;
            Entries = [];
            UnsupportedUntil = default;
        }
    }

    public async Task<IReadOnlyList<TranscriptEntry>?> ReadAsync(SessionInfo session, CancellationToken ct)
    {
        if (session.Phase != SessionStatus.Running || string.IsNullOrEmpty(session.PodIp)) return null;
        Sweep();
        var follow = _follows.GetOrAdd(session.Id, _ => new Follow());
        await follow.Gate.WaitAsync(ct);
        var downloadAcquired = false;
        try
        {
            // A slow pod must not queue other sessions behind it; their stored copy is ready.
            if (!await _downloads.WaitAsync(0, ct)) return null;
            downloadAcquired = true;
            follow.LastUsed = _now();
            if (follow.PodIp != session.PodIp) follow.Reset(session.PodIp);
            if (follow.UnsupportedUntil > _now()) return null;
            if (follow.Token is null)
            {
                // The callback token lives on the record, not on the public SessionInfo.
                follow.Token = (await _sessions.GetByIdAsync(session.Id, ct))?.CallbackToken;
                if (string.IsNullOrEmpty(follow.Token)) return null;
            }

            for (var round = 0; round < MaxRoundsPerRead; round++)
            {
                // Status can outlive a pod and its IP can be reused by another session.
                // Confirm immediately before each request; caching would reopen that window.
                using var confirmation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                confirmation.CancelAfter(TimeSpan.FromSeconds(5));
                if (!IPAddress.TryParse(session.PodIp, out var address) ||
                    !await _pods.IsLiveSessionPodAsync(session.Id, address, confirmation.Token)) return null;
                var chunk = await FetchAsync(session.PodIp, follow.Token, follow.NextByte, ct);
                if (chunk is null)
                {
                    return null;
                }
                if (chunk.Unsupported)
                {
                    follow.UnsupportedUntil = _now() + UnsupportedFor;
                    return null;
                }
                // A new conversation can be larger than the old one; size alone cannot identify it.
                if (chunk.Id is not null && chunk.Id != follow.TranscriptId)
                {
                    var hadCursor = follow.NextByte != 0;
                    follow.Reset(session.PodIp);
                    follow.TranscriptId = chunk.Id;
                    if (hadCursor) continue;
                }
                if (chunk.Size < follow.NextByte)
                {
                    follow.Reset(session.PodIp);
                    continue;
                }
                if (chunk.Text.Length > 0)
                {
                    follow.Entries = [.. NativeTranscript.Append(follow.Entries, chunk.Text)];
                }
                var advanced = chunk.Next > follow.NextByte;
                follow.NextByte = Math.Max(follow.NextByte, chunk.Next);
                if (!advanced || follow.NextByte >= chunk.Size) break;
            }
            return follow.Entries.Length > 0 ? follow.Entries : null;
        }
        catch (Exception e) when ((e is HttpRequestException or OperationCanceledException or k8s.Autorest.HttpOperationException) && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (downloadAcquired) _downloads.Release();
            follow.Gate.Release();
        }
    }

    private sealed record Chunk(bool Unsupported, long Size, long Next, string Text, string? Id = null);

    private async Task<Chunk?> FetchAsync(string podIp, string token, long from, CancellationToken ct)
    {
        var uri = new UriBuilder("http", podIp, _agentPort, "agenthub/transcript")
        {
            Query = "from=" + from.ToString(CultureInfo.InvariantCulture)
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Agent-Token", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new Chunk(true, 0, 0, "");
        if (!response.IsSuccessStatusCode) return null;
        if (!TryHeader(response, "X-Transcript-Size", out var size) ||
            !TryHeader(response, "X-Transcript-Next", out var next)) return null;
        // The pod cuts every chunk after a newline, and a newline byte never occurs inside a
        // UTF-8 sequence, so each chunk decodes on its own.
        // Bound the wire body too: an oversized response must not allocate without limit.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var body = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (body.Length + count > MaxChunkBytes) return null;
            body.Write(buffer, 0, count);
        }
        var id = response.Headers.TryGetValues("X-Transcript-Id", out var ids) ? ids.FirstOrDefault() : null;
        return new Chunk(false, size, next, Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length), id);
    }

    private static bool TryHeader(HttpResponseMessage response, string name, out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) &&
               long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
               value >= 0;
    }

    // Followed sessions stay in memory only while someone is looking: a session nobody has
    // polled for a while is dropped and read from zero if it is opened again.
    private void Sweep()
    {
        var now = _now();
        if (now - _lastSweep < TimeSpan.FromMinutes(1)) return;
        _lastSweep = now;
        foreach (var (id, follow) in _follows)
            if (now - follow.LastUsed > IdleEviction && follow.Gate.CurrentCount == 1)
                _follows.TryRemove(id, out _);
    }
}
