using System.Net.Http.Headers;

namespace AgentHub.Api.Files;

public sealed class SessionFilePreviewWorker(
    ISessionFileRegistry registry,
    ISessionFileService files,
    IOfficePreviewClient renderer,
    IHttpClientFactory httpFactory,
    SessionFileOptions options,
    ILogger<SessionFilePreviewWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.OfficePreview.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await ProcessNextAsync(stoppingToken);
            if (!processed)
                await Task.Delay(Math.Max(250, options.OfficePreview.PollMilliseconds), stoppingToken);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        if (!options.OfficePreview.Enabled) return false;
        var source = await registry.ClaimPreviewAsync(ct);
        if (source is null) return false;
        try
        {
            var actor = new SessionFileActor(source.SessionId, source.Owner, source.Owner, true, true);
            await using var office = await OpenSourceAsync(actor, source, ct);
            var pdf = await renderer.RenderAsync(office, source.Name, ct);
            var previewName = Path.GetFileNameWithoutExtension(source.Name) + ".preview.pdf";
            var reserved = await files.ReserveAsync(actor,
                new ReserveSessionFileCommand(previewName, "application/pdf", pdf.LongLength, null, "office-preview"), ct);
            await UploadAsync(actor, reserved, pdf, ct);
            var completed = await files.CompleteAsync(actor, reserved.File.Id, ct);
            await registry.LinkPreviewAsync(source.Id, completed.Id, succeeded: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            log.LogWarning("Office preview conversion failed for file {FileId}: {ErrorType}",
                source.Id, exception.GetType().Name);
            await registry.LinkPreviewAsync(source.Id, string.Empty, succeeded: false, ct);
        }
        return true;
    }

    private async Task<Stream> OpenSourceAsync(
        SessionFileActor actor, SessionFileRecord source, CancellationToken ct)
    {
        var opened = await files.OpenContentAsync(actor, source.Id, ct: ct);
        if (opened.Content is not null) return opened.Content;
        if (opened.RedirectUrl is null) throw new SessionFileException("file_content_expired");
        var response = await httpFactory.CreateClient("session-file-storage")
            .GetAsync(opened.RedirectUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new SessionFileException("storage_read_failed");
        }
        return new ResponseOwnedStream(await response.Content.ReadAsStreamAsync(ct), response);
    }

    private async Task UploadAsync(
        SessionFileActor actor, ReserveFileResult reserved, byte[] pdf, CancellationToken ct)
    {
        await using var content = new MemoryStream(pdf, writable: false);
        if (reserved.File.StorageKind == SessionFileStorageKind.Pod)
        {
            await files.PutPodContentAsync(actor, reserved.File.Id, content, content.Length, ct);
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, reserved.Upload.Url)
        {
            Content = new ByteArrayContent(pdf),
        };
        foreach (var header in reserved.Upload.Headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
            else request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var response = await httpFactory.CreateClient("session-file-storage")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new SessionFileException("storage_upload_failed");
    }

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this);
        }
    }
}
