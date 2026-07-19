using System.Net;
using System.Text;
using AgentHub.Api.Chat.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class TelegramClientTests
{
    /// <summary>Returns queued responses in order and records every request.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public int RequestCount { get; private set; }

        public StubHandler Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK)
        { _responses.Enqueue((status, body)); return this; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var (status, body) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static TelegramClient Client(StubHandler handler)
        => new(new StubFactory(handler),
               new TelegramOptions { Enabled = true, BotToken = "unit-test-token" },
               NullLogger<TelegramClient>.Instance);

    [Fact]
    public async Task SendMessage_ReturnsMessageId()
    {
        var handler = new StubHandler().Enqueue("""{"ok":true,"result":{"message_id":42}}""");
        var id = await Client(handler).SendMessageAsync("5", "hi", null, null, CancellationToken.None);
        Assert.Equal("42", id);
    }

    [Fact]
    public async Task GetUpdates_ReturnsRawElementsAndNextOffset()
    {
        var handler = new StubHandler().Enqueue(
            """{"ok":true,"result":[{"update_id":5,"message":{"text":"a"}},{"update_id":7,"message":{"text":"b"}}]}""");
        var (nextOffset, updates) = await Client(handler).GetUpdatesAsync(3, CancellationToken.None);

        Assert.Equal(8, nextOffset); // max(update_id)+1
        Assert.Equal(2, updates.Count);
        Assert.Contains("\"update_id\":5", updates[0]);
        Assert.Contains("\"update_id\":7", updates[1]);
    }

    [Fact]
    public async Task GetUpdates_NotOk_KeepsOffsetAndReturnsEmpty()
    {
        var handler = new StubHandler().Enqueue("""{"ok":false,"description":"Unauthorized"}""");
        var (nextOffset, updates) = await Client(handler).GetUpdatesAsync(3, CancellationToken.None);

        Assert.Equal(3, nextOffset);
        Assert.Empty(updates);
    }

    [Fact]
    public async Task Edit_NotModified_IsSuccess()
    {
        var handler = new StubHandler().Enqueue(
            """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""",
            HttpStatusCode.BadRequest);
        Assert.True(await Client(handler).EditMessageTextAsync("5", "12", "t", null, CancellationToken.None));
    }

    [Fact]
    public async Task Edit_MessageGone_IsFalse()
    {
        var handler = new StubHandler().Enqueue(
            """{"ok":false,"error_code":400,"description":"Bad Request: message to edit not found"}""",
            HttpStatusCode.BadRequest);
        Assert.False(await Client(handler).EditMessageTextAsync("5", "12", "t", null, CancellationToken.None));
    }

    [Fact]
    public async Task Edit_RateLimited_IsTransient_StaysTrue()
    {
        // 429 without retry_after: no retry, but the indicator loop must keep going.
        var handler = new StubHandler().Enqueue(
            """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 5"}""",
            HttpStatusCode.TooManyRequests);
        Assert.True(await Client(handler).EditMessageTextAsync("5", "12", "t", null, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Post_RetriesOnceOnRetryAfter()
    {
        var handler = new StubHandler()
            .Enqueue("""{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 0","parameters":{"retry_after":0}}""",
                HttpStatusCode.TooManyRequests)
            .Enqueue("""{"ok":true,"result":{"message_id":42}}""");

        var id = await Client(handler).SendMessageAsync("5", "hi", null, null, CancellationToken.None);

        Assert.Equal("42", id);
        Assert.Equal(2, handler.RequestCount);
    }
}
