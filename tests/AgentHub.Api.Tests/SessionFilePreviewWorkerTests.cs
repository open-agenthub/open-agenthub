using AgentHub.Api.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionFilePreviewWorkerTests
{
    [Fact]
    public async Task Disabled_renderer_does_not_claim_download_ready_sources()
    {
        var registry = new FakeRegistry(Source());
        var worker = Worker(registry, new FakeFiles(), new FakeRenderer(), enabled: false);

        Assert.False(await worker.ProcessNextAsync());
        Assert.Equal(0, registry.Claims);
        Assert.Null(registry.Linked);
    }

    [Fact]
    public async Task Successful_conversion_stores_and_links_a_pdf_derivative()
    {
        var registry = new FakeRegistry(Source());
        var files = new FakeFiles();
        var worker = Worker(registry, files, new FakeRenderer([0x25, 0x50, 0x44, 0x46]));

        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(("source", "preview", true), registry.Linked);
        Assert.Equal([0x25, 0x50, 0x44, 0x46], files.Uploaded);
        Assert.Equal("report.preview.pdf", files.ReservedName);
    }

    [Fact]
    public async Task Conversion_failure_marks_only_preview_failed()
    {
        var source = Source();
        var registry = new FakeRegistry(source);
        var worker = Worker(registry, new FakeFiles(), new FakeRenderer(error: true));

        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(("source", "", false), registry.Linked);
        Assert.Equal(SessionFileState.Ready, source.State);
    }

    private static SessionFilePreviewWorker Worker(
        FakeRegistry registry, FakeFiles files, FakeRenderer renderer, bool enabled = true) =>
        new(registry, files, renderer, new FakeHttpFactory(), new SessionFileOptions
        {
            OfficePreview = new OfficePreviewOptions { Enabled = enabled },
        }, NullLogger<SessionFilePreviewWorker>.Instance);

    private static SessionFileRecord Source() => new(
        "source", "s1", "alice", "report.docx", ".docx",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 4,
        SessionFileStorageKind.Pod, "source/report.docx", SessionFileState.Ready,
        SessionFilePreviewState.Queued, null, "alice", "user", DateTime.UtcNow,
        DateTime.UtcNow, null);

    private sealed class FakeRenderer(byte[]? pdf = null, bool error = false) : IOfficePreviewClient
    {
        public Task<byte[]> RenderAsync(Stream source, string fileName, CancellationToken ct = default) =>
            error ? throw new SessionFileException("office_preview_failed") : Task.FromResult(pdf ?? [1]);
    }

    private sealed class FakeFiles : ISessionFileService
    {
        public byte[] Uploaded { get; private set; } = [];
        public string? ReservedName { get; private set; }
        public Task<FileContentResult> OpenContentAsync(SessionFileActor actor, string fileId, bool allowRedirect = true, CancellationToken ct = default) =>
            Task.FromResult(new FileContentResult(new MemoryStream([1, 2, 3, 4]), null,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "report.docx", 4));
        public Task<ReserveFileResult> ReserveAsync(SessionFileActor actor, ReserveSessionFileCommand request, CancellationToken ct = default)
        {
            ReservedName = request.Name;
            return Task.FromResult(new ReserveFileResult(Preview(request.Size),
                new FileUploadDescriptor("proxy", "/upload", new Dictionary<string, string>())));
        }
        public async Task PutPodContentAsync(SessionFileActor actor, string fileId, Stream content, long? contentLength = null, CancellationToken ct = default)
        {
            using var output = new MemoryStream();
            await content.CopyToAsync(output, ct);
            Uploaded = output.ToArray();
        }
        public Task<SessionFileRecord> CompleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) =>
            Task.FromResult(Preview(Uploaded.LongLength) with { State = SessionFileState.Ready });
        private static SessionFileRecord Preview(long size) => new(
            "preview", "s1", "alice", "report.preview.pdf", ".pdf", "application/pdf", "application/pdf", size,
            SessionFileStorageKind.Pod, "preview/report.preview.pdf", SessionFileState.Reserved,
            SessionFilePreviewState.None, null, "alice", "office-preview", DateTime.UtcNow, null, null);
        public Task<IReadOnlyList<SessionFileRecord>> ListAsync(SessionFileActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation?> GetPresentationAsync(SessionFileActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation> SetPresentationAsync(SessionFileActor actor, string? fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeRegistry(SessionFileRecord? source) : ISessionFileRegistry
    {
        public int Claims { get; private set; }
        public (string Source, string Preview, bool Succeeded)? Linked { get; private set; }
        public Task<SessionFileRecord?> ClaimPreviewAsync(CancellationToken ct = default)
        {
            Claims++;
            var claimed = source;
            source = null;
            return Task.FromResult(claimed);
        }
        public Task LinkPreviewAsync(string sourceId, string previewId, bool succeeded, CancellationToken ct = default)
        { Linked = (sourceId, previewId, succeeded); return Task.CompletedTask; }
        public Task InitializeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task InsertAsync(SessionFileRecord file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFileRecord?> GetAsync(string sessionId, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionFileRecord>> ListAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFileUsage> GetUsageAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TransitionAsync(string sessionId, string fileId, SessionFileState expected, SessionFileState next, string? detectedMime, long? actualSize, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation?> GetPresentationAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation> SetPresentationAsync(string sessionId, string? fileId, string presenter, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionFileRecord>> ExpireReservationsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkSessionDeletedAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
