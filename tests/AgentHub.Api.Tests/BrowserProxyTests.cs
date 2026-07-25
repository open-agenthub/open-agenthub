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
        var client = new MemoryWebSocket("key");
        var upstream = new MemoryWebSocket("screen");

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
        var client = new MemoryWebSocket("key");
        var upstream = new MemoryWebSocket("screen");

        await BrowserProxy.RelayAsync(client, upstream, canWrite: true, CancellationToken.None);

        Assert.Equal(["key"], upstream.SentFrames);
        Assert.Equal(["screen"], client.SentFrames);
    }

    private sealed class MemoryWebSocket(params string[] incoming) : WebSocket
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
        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_incoming.Count == 0)
                return Task.FromResult(new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "done"));
            var bytes = _incoming.Dequeue();
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length,
                WebSocketMessageType.Binary, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer,
            WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SentFrames.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}