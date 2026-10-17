using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
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

    private readonly HttpClient _http;
    private readonly ISessionStore _sessions;
    private readonly int _agentPort;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, Follow> _follows = new();
    private DateTime _lastSweep;

    public LiveTranscriptReader(HttpClient http, ISessionStore sessions, IConfiguration configuration,
        Func<DateTime>? now = null)
    {
        _http = http;
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
        public long NextByte;
        public TranscriptEntry[] Entries = [];
        public DateTime UnsupportedUntil;
        public DateTime LastUsed;

        public void Reset(string podIp)
        {
            PodIp = podIp;
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
        try
        {
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
                if (chunk.Size < follow.NextByte)
                {
                    follow.Reset(session.PodIp);
                    continue;
                }
                if (chunk.Text.Length > 0)
                {
                    var added = NativeTranscript.Parse(chunk.Text);
                    if (added.Count > 0) follow.Entries = [.. follow.Entries, .. added];
                }
                var advanced = chunk.Next > follow.NextByte;
                follow.NextByte = Math.Max(follow.NextByte, chunk.Next);
                if (!advanced || follow.NextByte >= chunk.Size) break;
            }
            return follow.Entries.Length > 0 ? follow.Entries : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            follow.Gate.Release();
        }
    }

    private sealed record Chunk(bool Unsupported, long Size, long Next, string Text);

    private async Task<Chunk?> FetchAsync(string podIp, string token, long from, CancellationToken ct)
    {
        var uri = new UriBuilder("http", podIp, _agentPort, "agenthub/transcript")
        {
            Query = "from=" + from.ToString(CultureInfo.InvariantCulture)
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Agent-Token", token);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new Chunk(true, 0, 0, "");
        if (!response.IsSuccessStatusCode) return null;
        if (!TryHeader(response, "X-Transcript-Size", out var size) ||
            !TryHeader(response, "X-Transcript-Next", out var next)) return null;
        // The pod cuts every chunk after a newline, and a newline byte never occurs inside a
        // UTF-8 sequence, so each chunk decodes on its own.
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return new Chunk(false, size, next, Encoding.UTF8.GetString(bytes));
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
