using System.Net.Http.Headers;

namespace AgentHub.Api.Files;

public interface IOfficePreviewClient
{
    Task<byte[]> RenderAsync(Stream source, string fileName, CancellationToken ct = default);
}

public sealed class OfficePreviewClient(HttpClient client, SessionFileOptions options)
    : IOfficePreviewClient
{
    public async Task<byte[]> RenderAsync(
        Stream source, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream",
        });
        form.Add(content, "file", fileName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.OfficePreview.TimeoutSeconds)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "render") { Content = form };
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new SessionFileException("office_preview_failed");
        if (response.Content.Headers.ContentType?.MediaType != "application/pdf")
            throw new SessionFileException("office_preview_invalid_response");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await ReadBoundedAsync(stream, options.OfficePreview.MaxOutputBytes, timeout.Token);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maximum, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) return result.ToArray();
            total += read;
            if (total > maximum) throw new SessionFileException("office_preview_too_large");
            await result.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
