using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AgentHub.Api.Browser;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #177: a request carrying a session's callback token or credentials leaves only for an address
/// the Kubernetes API confirms, right before it, as that session's live agent pod.
/// </summary>
public class AgentPodRequestsTests
{
    private sealed class Pod : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public TimeSpan Delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"delivered\":\"pty\"}") };
        }
    }

    private sealed class Identity : IAgentPodIdentityResolver
    {
        public bool Live = true;
        public Exception? Error;
        public List<(string Session, IPAddress Address)> Asked { get; } = new();

        public Task<bool> IsLiveSessionPodAsync(string sessionId, IPAddress sourceIp, CancellationToken ct = default)
        {
            lock (Asked) Asked.Add((sessionId, sourceIp));
            return Error is null ? Task.FromResult(Live) : Task.FromException<bool>(Error);
        }
    }

    private static HttpRequestMessage Request(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Agent-Token", "secret-token");
        return request;
    }

    [Fact]
    public async Task AConfirmedPodGetsTheRequest_AskedForThatSessionAndAddress()
    {
        var pod = new Pod();
        var identity = new Identity();
        var pods = TestPods.Using(new HttpClient(pod), identity);

        using var response = await pods.SendAsync("s1", Request("http://10.0.0.7:7681/agenthub/diff"),
            HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(5), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(pod.Requests);
        Assert.Equal(("s1", IPAddress.Parse("10.0.0.7")), Assert.Single(identity.Asked));
    }

    [Fact]
    public async Task AnAddressThatIsNotTheLivePod_GetsNothing()
    {
        var pod = new Pod();
        var pods = TestPods.Using(new HttpClient(pod), new Identity { Live = false });

        await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() => pods.SendAsync("s1",
            Request("http://10.0.0.7:7681/agenthub/diff"), HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(5), default));

        Assert.Empty(pod.Requests);
    }

    [Fact]
    public async Task AnApiServerThatCannotAnswer_CountsAsNotConfirmed()
    {
        var pod = new Pod();
        var pods = TestPods.Using(new HttpClient(pod), new Identity
        {
            Error = new k8s.Autorest.HttpOperationException("forbidden")
        });

        var error = await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() => pods.SendAsync("s1",
            Request("http://10.0.0.7:7681/x"), HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(5), default));

        Assert.IsType<k8s.Autorest.HttpOperationException>(error.InnerException);
        Assert.Empty(pod.Requests);
    }

    [Fact]
    public async Task OnlyAnIpAddressCanBeConfirmed_AndIpv6Works()
    {
        var pod = new Pod();
        var identity = new Identity();
        var pods = TestPods.Using(new HttpClient(pod), identity);

        // A name resolves to whatever DNS says at send time, after the check.
        await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() => pods.SendAsync("s1",
            Request("http://pod.example.com:7681/x"), HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(5), default));
        Assert.Empty(identity.Asked);

        using var response = await pods.SendAsync("s1", Request("http://[fd00::7]:7681/x"),
            HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(5), default);
        Assert.Equal(IPAddress.Parse("fd00::7"), Assert.Single(identity.Asked).Address);
        Assert.Single(pod.Requests);
    }

    [Fact]
    public async Task EachCallBringsItsOwnTimeout()
    {
        var pods = TestPods.Using(new HttpClient(new Pod { Delay = TimeSpan.FromSeconds(30) }), new Identity());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pods.SendAsync("s1",
            Request("http://10.0.0.7:7681/x"), HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromMilliseconds(50), default));
    }

    /// <summary>The registered client: a confirmed pod answering 307 must not get the token sent
    /// on to an address nobody confirmed.</summary>
    [Fact]
    public async Task TheRegisteredClientDoesNotFollowARedirect()
    {
        using var elsewhere = new TcpListener(IPAddress.Loopback, 0);
        elsewhere.Start();
        var elsewherePort = ((IPEndPoint)elsewhere.LocalEndpoint).Port;
        using var confirmed = new TcpListener(IPAddress.Loopback, 0);
        confirmed.Start();
        var confirmedPort = ((IPEndPoint)confirmed.LocalEndpoint).Port;
        var answering = Task.Run(async () =>
        {
            using var client = await confirmed.AcceptTcpClientAsync();
            var stream = client.GetStream();
            // Read the request head to its end before answering.
            var head = new List<byte>();
            var one = new byte[1];
            while (!head.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()) && await stream.ReadAsync(one) == 1) head.Add(one[0]);
            var reply = $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{elsewherePort}/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
        });

        var services = new ServiceCollection();
        services.AddSingleton<IAgentPodIdentityResolver>(new Identity());
        services.AddAgentPodRequests();
        using var provider = services.BuildServiceProvider();
        var pods = provider.GetRequiredService<IAgentPodRequests>();

        using var response = await pods.SendAsync("s1", Request($"http://127.0.0.1:{confirmedPort}/agenthub/diff"),
            HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(10), default);
        await answering;

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.False(elsewhere.Pending(), "the redirect target was contacted");
    }

    // ---- every caller that sends the token, against an address that is not the live pod ----

    private class Fake<T> : DispatchProxy where T : class
    {
        public Dictionary<string, Func<object?>> Answers = new();

        public static T Create(Dictionary<string, Func<object?>> answers)
        {
            var proxy = Create<T, Fake<T>>();
            ((Fake<T>)(object)proxy).Answers = answers;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Answers.TryGetValue(method!.Name, out var answer) ? answer() : throw new NotSupportedException(method.Name);
    }

    private static readonly SessionInfo Stale = new()
    {
        Id = "s1", Title = "t", Owner = "owner", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "10.0.0.7",
        Agent = AgentKind.Claude, UiMode = SessionUiMode.Terminal
    };

    private static SessionRecord Record => new() { Id = "s1", Owner = "owner", CallbackToken = "secret-token" };

    [Fact]
    public async Task EveryCallerThatSendsTheToken_SendsNothingToAnAddressThatIsNotTheLivePod()
    {
        var pod = new Pod();
        var pods = TestPods.Using(new HttpClient(pod), new Identity { Live = false });
        var config = new ConfigurationBuilder().Build();
        var sessions = Fake<ISessionService>.Create(new() { ["GetSessionAsync"] = () => Task.FromResult<SessionInfo?>(Stale) });
        var store = Fake<ISessionStore>.Create(new()
        {
            ["GetAsync"] = () => Task.FromResult<SessionRecord?>(Record),
            ["GetByIdAsync"] = () => Task.FromResult<SessionRecord?>(Record)
        });
        var file = new SessionFileRecord("f1", "s1", "owner", "a.txt", ".txt", "text/plain", null, 1, default, "",
            default, default, null, "owner", "agent", DateTime.UtcNow, null, null);
        var pusher = new AgentCredentialPusher(pods, config);

        Assert.Null(await new WorkspaceDiffReader(pods, sessions, config).ReadAsync(Record, "head", default));
        Assert.Null(await new SessionCommandsReader(pods, sessions, config).ReadAsync(Record, default));
        Assert.Null(await new SessionTerminalAttachments(pods, sessions, config).MaterializeAsync(Record, ["f1"], default));
        Assert.Null(await new LiveTranscriptReader(pods, store, config).ReadAsync(Stale, default));
        var delivery = await new SessionMessageDelivery(pods, store, Fake<ISessionMessageStore>.Create(new()), config,
                NullLogger<SessionMessageDelivery>.Instance)
            .TryInjectAsync(Stale, new SessionMessageRecord { Id = "m1", ToSessionId = "s1", Owner = "owner", Body = "hi" }, null, default);
        Assert.Equal((MessageDeliveryVia.Inbox, "pod_not_confirmed"), (delivery.Via, delivery.Reason));
        await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() =>
            new AgentFileClient(pods, store, config).ExistsAsync(Stale, file, default));
        await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() =>
            pusher.PushAsync("s1", "10.0.0.7", "secret-token", AgentKind.Claude, [1], default));
        await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() =>
            pusher.RefreshAsync("s1", "10.0.0.7", "secret-token", AgentKind.Claude, [1], default));
        await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() =>
            pusher.PushGitCredentialsAsync("s1", "10.0.0.7", "secret-token", [1], default));
        var clear = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SessionContextClear(pods, store, config).ScheduleAsync(Stale, new ContextClearRequest("handover"), default));
        Assert.Equal("session_not_running", clear.Message);

        Assert.Empty(pod.Requests);
    }
}
