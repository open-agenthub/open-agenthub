using System.Net.Http.Json;

namespace AgentHub.Api.Browser;

public interface IBrowserRuntimeClient
{
    Task ResizeAsync(
        string podIp, BrowserViewport viewport, CancellationToken ct = default);

    Task PasteAsync(string podIp, string text, CancellationToken ct = default);

    Task<string> CopyAsync(string podIp, bool cut, CancellationToken ct = default);
}

public sealed class BrowserRuntimeClient(HttpClient client) : IBrowserRuntimeClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public async Task ResizeAsync(
        string podIp, BrowserViewport viewport, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.PutAsJsonAsync(Control(podIp, "viewport"), new
        {
            width = viewport.Width,
            height = viewport.Height
        }, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    public async Task PasteAsync(string podIp, string text, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.PostAsJsonAsync(
            Control(podIp, "clipboard/paste"), new { text }, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> CopyAsync(string podIp, bool cut, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.PostAsJsonAsync(
            Control(podIp, "clipboard/copy"), new { cut }, timeout.Token);
        response.EnsureSuccessStatusCode();
        var copied = await response.Content.ReadFromJsonAsync<BrowserClipboardText>(timeout.Token);
        return copied?.Text ?? "";
    }

    private static Uri Control(string podIp, string path) =>
        new UriBuilder(Uri.UriSchemeHttp, podIp, 6081, path).Uri;
}
