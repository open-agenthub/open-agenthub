using System.Net.WebSockets;
using System.Text;
using AgentHub.Api.WebSockets;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserProxyTests
{
    [Fact]
    public async Task Viewer_PumpsProtocolToDedicatedViewOnlyUpstream()
    {
        var rendezvous = new FrameRendezvous(expectedFrames: 2);
        var client = new MemoryWebSocket(rendezvous, "key");
        var upstream = new MemoryWebSocket(rendezvous, "screen");

        await BrowserProxy.RelayAsync(client, upstream, canWrite: false, CancellationToken.None);

        Assert.Equal(["key"], upstream.SentFrames);
        Assert.Equal(["screen"], client.SentFrames);
    }

    [Fact]
    public void Viewer_UsesDedicatedViewOnlyPort()
    {
        Assert.Equal(6082, BrowserProxy.UpstreamPort(canWrite: false));
        Assert.Equal(6080, BrowserProxy.UpstreamPort(canWrite: true));
    }

    [Fact]
    public async Task Collaborator_PumpsBothDirections()
    {
        var rendezvous = new FrameRendezvous(expectedFrames: 2);
        var client = new MemoryWebSocket(rendezvous, "key");
        var upstream = new MemoryWebSocket(rendezvous, "screen");

        await BrowserProxy.RelayAsync(client, upstream, canWrite: true, CancellationToken.None);

        Assert.Equal(["key"], upstream.SentFrames);
        Assert.Equal(["screen"], client.SentFrames);
    }

    [Fact]
    public async Task AuthorizationMonitor_FailsClosedWhenAccessIsRevoked()
    {
        var calls = 0;
        var authorized = await BrowserProxy.MonitorAuthorizationAsync(_ =>
        {
            calls++;
            return Task.FromResult(calls < 2);
        }, CancellationToken.None, TimeSpan.Zero);

        Assert.False(authorized);
        Assert.Equal(2, calls);
    }
    /// <summary>
    /// Holds both sockets open until every expected frame has been forwarded.
    ///
    /// RelayAsync stops as soon as the *first* direction finishes and cancels the other —
    /// correct behaviour, but it means a socket that reports "closed" the moment its queue
    /// runs dry can tear the relay down before the opposite pump has even been scheduled
    /// (PumpAsync starts with an await). Without this gate the relay tests pass on a fast
    /// machine and fail on a loaded CI runner.
    /// </summary>
    private sealed class FrameRendezvous(int expectedFrames)
    {
        private readonly TaskCompletionSource _allDelivered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = expectedFrames;

        public Task AllDelivered => _allDelivered.Task;

        public void Delivered()
        {
            if (Interlocked.Decrement(ref _remaining) == 0) _allDelivered.TrySetResult();
        }
    }

    private sealed class MemoryWebSocket(FrameRendezvous rendezvous, params string[] incoming) : WebSocket
    {
        private readonly Queue<byte[]> _incoming = new(incoming.Select(Encoding.UTF8.GetBytes));
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;
        public List<string> SentFrames { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => "binary";
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus,
            string? statusDescription, CancellationToken cancellationToken)
        { _closeStatus = closeStatus; _closeDescription = statusDescription; _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus,
            string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override void Dispose() => _state = WebSocketState.Closed;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_incoming.Count == 0)
            {
                // Only report the close once the other direction has delivered too.
                await rendezvous.AllDelivered.WaitAsync(cancellationToken);
                return new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "done");
            }
            var bytes = _incoming.Dequeue();
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Binary, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer,
            WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentFrames.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            rendezvous.Delivered();
            return Task.CompletedTask;
        }
    }
}