using System.Net;
using System.Text.Json;
using AgentHub.Api.Browser;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserRuntimeClientTests
{
    [Theory]
    [InlineData(480, 320, true)]
    [InlineData(2560, 1600, true)]
    [InlineData(479, 320, false)]
    [InlineData(480, 319, false)]
    [InlineData(2561, 1600, false)]
    [InlineData(2560, 1601, false)]
    public void ViewportValidation_EnforcesInclusiveResourceBounds(
        int width, int height, bool expected)
    {
        Assert.Equal(expected, BrowserViewport.TryCreate(width, height, out var viewport));
        Assert.Equal(expected, viewport is not null);
    }

    [Fact]
    public void BrowserDefaultsProvisionTheLargestAcceptedViewport()
    {
        var options = new BrowserOptions();

        Assert.Equal(BrowserViewport.MaxWidth, options.ScreenWidth);
        Assert.Equal(BrowserViewport.MaxHeight, options.ScreenHeight);
    }

    [Fact]
    public async Task Resize_SendsExactViewportToTheSelectedPodControlPort()
    {
        var handler = new RecordingHandler();
        var client = new BrowserRuntimeClient(new HttpClient(handler));

        await client.ResizeAsync("10.0.0.9", new BrowserViewport(800, 600));

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("http://10.0.0.9:6081/viewport", handler.Uri?.AbsoluteUri);
        var payload = JsonSerializer.Deserialize<JsonElement>(handler.Body);
        Assert.Equal(800, payload.GetProperty("width").GetInt32());
        Assert.Equal(600, payload.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Resize_PropagatesRuntimeFailure()
    {
        var client = new BrowserRuntimeClient(new HttpClient(
            new RecordingHandler(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ResizeAsync("10.0.0.9", new BrowserViewport(800, 600)));
    }

    [Fact]
    public async Task Paste_PostsTheTextToTheSupervisor()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var client = new BrowserRuntimeClient(http);

        await client.PasteAsync("10.0.0.9", "hello \"world\"\n");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://10.0.0.9:6081/clipboard/paste", handler.Uri?.AbsoluteUri);
        var payload = JsonSerializer.Deserialize<JsonElement>(handler.Body);
        Assert.Equal("hello \"world\"\n", payload.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Copy_ReturnsTheSelectionTheSupervisorRead()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"text":"picked"}""");
        using var http = new HttpClient(handler);
        var client = new BrowserRuntimeClient(http);

        Assert.Equal("picked", await client.CopyAsync("10.0.0.9", cut: true));
        Assert.Equal("http://10.0.0.9:6081/clipboard/copy", handler.Uri?.AbsoluteUri);
        Assert.True(JsonSerializer.Deserialize<JsonElement>(handler.Body).GetProperty("cut").GetBoolean());
    }

    [Fact]
    public async Task Copy_KeepsTheRuntimeStatusForTheCaller()
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.RequestEntityTooLarge));
        var client = new BrowserRuntimeClient(http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CopyAsync("10.0.0.9", cut: false));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, error.StatusCode);
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode = HttpStatusCode.NoContent, string? responseJson = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = responseJson is null
                    ? null
                    : new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
