using System.Net.Http.Json;

namespace AgentHub.Api.Browser;

public interface IBrowserRuntimeClient
{
    Task ResizeAsync(
        string podIp, BrowserViewport viewport, CancellationToken ct = default);
}

public sealed class BrowserRuntimeClient(HttpClient client) : IBrowserRuntimeClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public async Task ResizeAsync(
        string podIp, BrowserViewport viewport, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var uri = new UriBuilder(
            Uri.UriSchemeHttp, podIp, 6081, "viewport").Uri;
        using var response = await client.PutAsJsonAsync(uri, new
        {
            width = viewport.Width,
            height = viewport.Height
        }, timeout.Token);
        response.EnsureSuccessStatusCode();
    }
}
