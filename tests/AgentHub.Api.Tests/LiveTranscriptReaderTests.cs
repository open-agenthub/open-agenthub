using System.Net;
using System.Text;
using AgentHub.Api.Browser;
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
        public string? TranscriptId;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool More;
        public readonly List<Uri> Requests = [];
        public void Append(string text) => File = [.. File, .. Encoding.UTF8.GetBytes(text)];

        // Async so each response is returned to the caller, which owns and disposes it, rather than
        // wrapped in a Task the analyzer cannot follow.
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            Requests.Add(request.RequestUri!);
            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status);
            Assert.Equal(Token, request.Headers.GetValues("X-Agent-Token").Single());
            var from = long.Parse(request.RequestUri!.Query.Split('=')[1]);
            var body = from >= File.Length ? [] : File[(int)from..];
            var whole = Array.LastIndexOf(body, (byte)'\n') + 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body[..whole]) };
            if (TranscriptId is not null) response.Headers.Add("X-Transcript-Id", TranscriptId);
            response.Headers.Add("X-Transcript-Size", (File.Length + (More ? 1 : 0)).ToString());
            response.Headers.Add("X-Transcript-Next", (Math.Min(from, File.Length) + whole).ToString());
            return response;
        }
    }

    private sealed class Identity : IAgentPodIdentityResolver
    {
        public bool Live = true;
        public int Calls;
        public int AllowedCalls = int.MaxValue;
        public Task<bool> IsLiveSessionPodAsync(string id, IPAddress ip, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Live && Calls <= AllowedCalls);
        }
    }

    [Fact]
    public async Task LargerReplacementConversationRestartsAtZeroInsteadOfKeepingTheOldCursor()
    {
        var pod = new Pod { TranscriptId = "old" };
        pod.Append(Line("user", "old"));
        var reader = new LiveTranscriptReader(new HttpClient(pod), new Store(),
            new ConfigurationBuilder().Build(), new Identity());
        Assert.Equal("old", Assert.Single((await reader.ReadAsync(Running(), CancellationToken.None))!).Text);
        pod.File = [];
        pod.TranscriptId = "new";
        pod.Append("{\"type\":\"agenthub_conversation\",\"id\":\"new\"}\n" + Line("user", "a larger new conversation"));
        var entries = await reader.ReadAsync(Running(), CancellationToken.None);
        Assert.Equal(new[] { "Conversation cleared", "a larger new conversation" }, entries!.Select(entry => entry.Text));
        Assert.Equal("?from=0", pod.Requests.Last().Query);
    }

    [Fact]
    public async Task StaleIpNeverReceivesTheCallbackTokenEvenAfterASuccessfulRead()
    {
        var pod = new Pod();
        pod.Append(Line("user", "hello"));
        var identity = new Identity();
        var reader = new LiveTranscriptReader(new HttpClient(pod), new Store(),
            new ConfigurationBuilder().Build(), identity);
        Assert.NotNull(await reader.ReadAsync(Running(), CancellationToken.None));
        identity.Live = false;
        Assert.Null(await reader.ReadAsync(Running(), CancellationToken.None));
        Assert.Single(pod.Requests);
        Assert.Equal(2, identity.Calls);
    }

    [Fact]
    public async Task RealHttp_StaleIpGetsNoTokenAndFreshPodGetsTheTranscript()
    {
        // Exercise the SDK's actual wire request and the reader's actual HTTP transport.
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var live = false;
        var tokenRequests = 0;
        var identityRequests = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    string body;
                    if (context.Request.Url!.AbsolutePath.EndsWith("/pods"))
                    {
                        identityRequests++;
                        Assert.Null(context.Request.Headers["X-Agent-Token"]);
                        Assert.Contains("metadata.name=session-s1", context.Request.QueryString["fieldSelector"]);
                        var ip = live ? "127.0.0.1" : "127.0.0.2";
                        body = $$$"""{"apiVersion":"v1","kind":"PodList","metadata":{},"items":[{"metadata":{"name":"session-s1","uid":"current-pod","labels":{"agenthub.dev/session":"s1","agenthub.dev/component":"agent"}},"status":{"phase":"Running","podIP":"{{{ip}}}"}}]}""";
                    }
                    else
                    {
                        tokenRequests++;
                        Assert.Equal(Token, context.Request.Headers["X-Agent-Token"]);
                        body = Line("user", "real HTTP");
                        var length = Encoding.UTF8.GetByteCount(body).ToString();
                        context.Response.Headers.Add("X-Transcript-Size", length);
                        context.Response.Headers.Add("X-Transcript-Next", length);
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, stop.Token);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException e) when (stop.IsCancellationRequested)
            {
                // Only the fixture shutdown may terminate the HTTP accept loop.
                Assert.Equal(stop.Token, e.CancellationToken);
            }
        }, stop.Token);
        try
        {
            using var client = new k8s.Kubernetes(new k8s.KubernetesClientConfiguration
                { Host = $"http://127.0.0.1:{port}" });
            using var http = new HttpClient();
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["AgentHub:AgentPort"] = port.ToString() }).Build();
            var reader = new LiveTranscriptReader(http, new Store(), config,
                new KubernetesAgentPodIdentityResolver(client, "sessions"));
            Assert.Null(await reader.ReadAsync(Running("127.0.0.1"), stop.Token));
            Assert.Equal(0, tokenRequests);
            live = true;
            var entries = await reader.ReadAsync(Running("127.0.0.1"), stop.Token);
            Assert.Equal("real HTTP", Assert.Single(entries!).Text);
            live = false;
            Assert.Null(await reader.ReadAsync(Running("127.0.0.1"), stop.Token));
            Assert.Equal(1, tokenRequests);
            Assert.Equal(3, identityRequests);
        }
        finally
        {
            await stop.CancelAsync();
            await server;
        }
    }

    [Fact]
    public async Task OversizedBodyFallsBackWithoutParsingIt()
    {
        var (reader, pod, _, _) = Create();
        pod.File = Encoding.UTF8.GetBytes(new string('x', 8 * 1024 * 1024) + "\n");
        Assert.Null(await reader.ReadAsync(Running(), CancellationToken.None));
        Assert.Single(pod.Requests);
    }

    [Fact]
    public async Task RechecksIdentityBetweenCatchUpRounds()
    {
        using var pod = new Pod { More = true };
        pod.Append(Line("user", "hello"));
        using var http = new HttpClient(pod);
        var identity = new Identity { AllowedCalls = 1 };
        var reader = new LiveTranscriptReader(http, new Store(), new ConfigurationBuilder().Build(), identity);
        Assert.Null(await reader.ReadAsync(Running(), CancellationToken.None));
        Assert.Single(pod.Requests);
        Assert.Equal(2, identity.Calls);
    }

    private sealed class BusyPod : HttpMessageHandler
    {
        public readonly TaskCompletionSource OneStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _peakLock = new();
        public int Active;
        public int Peak;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref Active);
            lock (_peakLock) Peak = Math.Max(Peak, active);
            if (active == 1) OneStarted.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(ct);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Line("user", "hello")) };
                var size = (await response.Content.ReadAsByteArrayAsync(ct)).Length.ToString();
                response.Headers.Add("X-Transcript-Size", size);
                response.Headers.Add("X-Transcript-Next", size);
                return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    [Fact]
    public async Task BusyLiveReadSendsOtherSessionsToStoredCopyWithoutWaiting()
    {
        using var pod = new BusyPod();
        using var http = new HttpClient(pod);
        var reader = new LiveTranscriptReader(http, new Store(), new ConfigurationBuilder().Build(), new Identity());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reads = Enumerable.Range(0, 24).Select(i =>
            reader.ReadAsync(Running() with { Id = "s" + i }, stop.Token)).ToArray();
        try
        {
            await pod.OneStarted.Task.WaitAsync(stop.Token);
            Assert.Equal(1, pod.Active);
            var otherReads = await Task.WhenAll(reads.Skip(1)).WaitAsync(stop.Token);
            Assert.All(otherReads, entries => Assert.Null(entries));
        }
        finally { pod.Release.TrySetResult(); }
        Assert.NotNull(await reads[0]);
        Assert.NotNull(await reader.ReadAsync(Running() with { Id = "after" }, stop.Token));
        Assert.Equal(1, pod.Peak);
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
        var reader = new LiveTranscriptReader(new HttpClient(pod), store, new ConfigurationBuilder().Build(), new Identity(), () => now);
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
