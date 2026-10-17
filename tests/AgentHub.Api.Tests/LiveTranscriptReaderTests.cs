using System.Net;
using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The hub following a running session's transcript in its pod (#111). The pod side of the
/// protocol is agent-runtime/session-agent/test/common-server-transcript.test.js; this stand-in
/// answers the same way: whole lines from a byte offset, the file size, and the next offset.
/// </summary>
public class LiveTranscriptReaderTests
{
    private const string Token = "callback-token";

    private static string Line(string role, string text) =>
        $"{{\"type\":\"{role}\",\"message\":{{\"role\":\"{role}\",\"content\":\"{text}\"}}}}\n";

    private static SessionInfo Running(string podIp = "10.0.0.5", string phase = SessionStatus.Running) => new()
    {
        Id = "s1", Owner = "alice", Title = "t", Phase = phase, PodIp = podIp,
        Agent = AgentKind.Claude, Mode = SessionMode.Interactive
    };

    private sealed class Pod : HttpMessageHandler
    {
        public byte[] File = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public readonly List<Uri> Requests = [];
        public void Append(string text) => File = [.. File, .. Encoding.UTF8.GetBytes(text)];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            Assert.Equal(Token, request.Headers.GetValues("X-Agent-Token").Single());
            var from = long.Parse(request.RequestUri!.Query.Split('=')[1]);
            var body = from >= File.Length ? [] : File[(int)from..];
            var whole = Array.LastIndexOf(body, (byte)'\n') + 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body[..whole]) };
            response.Headers.Add("X-Transcript-Size", File.Length.ToString());
            response.Headers.Add("X-Transcript-Next", (Math.Min(from, File.Length) + whole).ToString());
            return Task.FromResult(response);
        }
    }

    private sealed class Store : ISessionStore
    {
        public int Lookups;
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            Lookups++;
            return Task.FromResult<SessionRecord?>(new SessionRecord { Id = id, Owner = "alice", CallbackToken = Token });
        }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => GetByIdAsync(id, ct);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListExpiredAsync(DateTime now, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (LiveTranscriptReader Reader, Pod Pod, Store Store, Action<TimeSpan> Advance) Create()
    {
        var pod = new Pod();
        var store = new Store();
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var reader = new LiveTranscriptReader(new HttpClient(pod), store, new ConfigurationBuilder().Build(), () => now);
        return (reader, pod, store, by => now += by);
    }

    [Fact]
    public async Task FollowsTheFileByOffsetAndAppendsOnlyWhatIsNew()
    {
        var (reader, pod, store, _) = Create();
        pod.Append(Line("user", "hello") + Line("assistant", "hi"));

        var first = await reader.ReadAsync(Running(), CancellationToken.None);
        Assert.Equal(["hello", "hi"], first!.Select(e => e.Text));

        pod.Append(Line("user", "next") + "{\"type\":\"assis");
        var second = await reader.ReadAsync(Running(), CancellationToken.None);
        Assert.Equal(["hello", "hi", "next"], second!.Select(e => e.Text));
        var firstTwo = Encoding.UTF8.GetByteCount(Line("user", "hello") + Line("assistant", "hi"));
        Assert.EndsWith("from=" + firstTwo, pod.Requests[1].Query);

        // The half-written line arrives once it is finished, exactly once.
        pod.File = [.. pod.File[..^"{\"type\":\"assis".Length]];
        pod.Append(Line("assistant", "done"));
        var third = await reader.ReadAsync(Running(), CancellationToken.None);
        Assert.Equal(["hello", "hi", "next", "done"], third!.Select(e => e.Text));
        Assert.Equal(1, store.Lookups);
    }

    [Fact]
    public async Task StartsOverWhenTheFileGotShorterThanTheCursor()
    {
        var (reader, pod, _, _) = Create();
        pod.Append(Line("user", "a") + Line("assistant", "b") + Line("user", "c"));
        await reader.ReadAsync(Running(), CancellationToken.None);

        pod.File = Encoding.UTF8.GetBytes(Line("user", "fresh"));
        var entries = await reader.ReadAsync(Running(), CancellationToken.None);

        Assert.Equal(["fresh"], entries!.Select(e => e.Text));
    }

    [Fact]
    public async Task StartsOverForANewPod()
    {
        var (reader, pod, _, _) = Create();
        pod.Append(Line("user", "a"));
        await reader.ReadAsync(Running("10.0.0.5"), CancellationToken.None);

        var entries = await reader.ReadAsync(Running("10.0.0.9"), CancellationToken.None);

        Assert.Equal(["a"], entries!.Select(e => e.Text));
        Assert.EndsWith("from=0", pod.Requests[^1].Query);
        Assert.Equal("10.0.0.9", pod.Requests[^1].Host);
    }

    [Fact]
    public async Task AnOlderRuntimeWithoutTheRouteIsNotAskedAgainOnEveryPoll()
    {
        var (reader, pod, _, advance) = Create();
        pod.Status = HttpStatusCode.NotFound;

        Assert.Null(await reader.ReadAsync(Running(), CancellationToken.None));
        Assert.Null(await reader.ReadAsync(Running(), CancellationToken.None));
        Assert.Single(pod.Requests);

        advance(TimeSpan.FromMinutes(6));
        pod.Status = HttpStatusCode.OK;
        pod.Append(Line("user", "a"));
        Assert.NotNull(await reader.ReadAsync(Running(), CancellationToken.None));
    }

    [Theory]
    [InlineData(SessionStatus.Paused, "10.0.0.5")]
    [InlineData(SessionStatus.Running, "")]
    public async Task ReadsNothingLiveWithoutARunningPod(string phase, string podIp)
    {
        var (reader, pod, _, _) = Create();
        pod.Append(Line("user", "a"));
        Assert.Null(await reader.ReadAsync(Running(podIp, phase), CancellationToken.None));
        Assert.Empty(pod.Requests);
    }

    [Fact]
    public async Task AnUnreachablePodSendsTheCallerToTheStoredCopy()
    {
        var (reader, pod, _, _) = Create();
        pod.Status = HttpStatusCode.BadGateway;
        Assert.Null(await reader.ReadAsync(Running(), CancellationToken.None));
    }

    [Fact]
    public async Task ConversationPage_PrefersTheLiveTranscriptForARunningSession()
    {
        var live = new List<TranscriptEntry> { new("user", "live", null) };
        var page = await SessionTranscripts.PageAsync(null!, "alice", "s1", SessionStatus.Running, 0, null,
            CancellationToken.None, _ => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(live));

        Assert.Equal(ConversationPage.NativeSource, page!.Source);
        Assert.Equal("live", Assert.Single(page.Entries).Text);
    }
}
