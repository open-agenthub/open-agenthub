using System.IO.Compression;
using System.Text;
using AgentHub.Api.Files;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionFileValidationTests
{
    [Theory]
    [InlineData("shot.png", "image/png", 20 * 1024 * 1024, true)]
    [InlineData("shot.png", "image/png", 20 * 1024 * 1024 + 1L, false)]
    [InlineData("payload.svg", "image/svg+xml", 128, false)]
    [InlineData("report.pdf", "application/pdf", 50 * 1024 * 1024, true)]
    [InlineData("report.pdf", "application/pdf", 50 * 1024 * 1024 + 1L, false)]
    public void Reservation_enforces_the_allowlist_and_size_limit(
        string name, string mime, long size, bool allowed)
    {
        var result = SessionFileValidator.ValidateReservation(
            new SessionFileOptions(), name, mime, size);

        Assert.Equal(allowed, result.Allowed);
    }

    [Fact]
    public void Reservation_rejects_a_mime_type_that_does_not_match_the_extension()
    {
        var result = SessionFileValidator.ValidateReservation(
            new SessionFileOptions(), "shot.png", "application/pdf", 128);

        Assert.Equal("content_type_mismatch", result.Code);
    }
    [Theory]
    [InlineData("shot?.png")]
    [InlineData("folder/shot.png")]
    [InlineData("folder\\shot.png")]
    public void Reservation_rejects_unsafe_cross_platform_file_names(string name)
    {
        var result = SessionFileValidator.ValidateReservation(
            new SessionFileOptions(), name, "image/png", 128);

        Assert.Equal("unsupported_file_type", result.Code);
    }


    [Fact]
    public void Chat_batch_rejects_six_files_even_when_total_bytes_are_small()
    {
        var files = Enumerable.Range(0, 6)
            .Select(i => ReadyFile($"f{i}", 10))
            .ToArray();

        Assert.Equal("attachment_count_exceeded",
            SessionFileValidator.ValidateBatch(new SessionFileOptions(), files).Code);
    }

    [Fact]
    public void Chat_batch_rejects_more_than_fifty_mebibytes()
    {
        var files = new[]
        {
            ReadyFile("first", 30L * 1024 * 1024),
            ReadyFile("second", 20L * 1024 * 1024 + 1),
        };

        Assert.Equal("attachment_bytes_exceeded",
            SessionFileValidator.ValidateBatch(new SessionFileOptions(), files).Code);
    }

    public static TheoryData<string, string, byte[]> DetectedTypes => new()
    {
        { "shot.png", "image/png", [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a] },
        { "photo.jpg", "image/jpeg", [0xff, 0xd8, 0xff, 0xe0] },
        { "old.gif", "image/gif", Encoding.ASCII.GetBytes("GIF87a") },
        { "new.gif", "image/gif", Encoding.ASCII.GetBytes("GIF89a") },
        { "clip.webp", "image/webp", WebpBytes() },
        { "paper.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7") },
        { "notes.md", "text/markdown", Encoding.UTF8.GetBytes("# Heading\n\nText") },
        { "notes.txt", "text/plain", Encoding.UTF8.GetBytes("Plain UTF-8 text") },
    };

    [Theory]
    [MemberData(nameof(DetectedTypes))]
    public async Task Detection_recognizes_allowed_magic_bytes(
        string name, string expectedMime, byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);

        var result = await SessionFileValidator.DetectAsync(name, stream);

        Assert.True(result.Allowed);
        Assert.Equal(expectedMime, result.DetectedMimeType);
    }

    [Theory]
    [InlineData("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")]
    [InlineData("letter.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")]
    public async Task Detection_recognizes_the_office_package_from_content_types(
        string name, string expectedMime, string packageContentType)
    {
        await using var stream = OfficePackage(packageContentType);

        var result = await SessionFileValidator.DetectAsync(name, stream);

        Assert.True(result.Allowed);
        Assert.Equal(expectedMime, result.DetectedMimeType);
    }

    [Fact]
    public async Task Detection_rejects_magic_bytes_that_do_not_match_the_name()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7"));

        var result = await SessionFileValidator.DetectAsync("shot.png", stream);

        Assert.Equal("content_type_mismatch", result.Code);
    }

    private static SessionFileRecord ReadyFile(string id, long size) => new(
        id, "session", "owner", $"{id}.png", ".png", "image/png", "image/png", size,
        SessionFileStorageKind.Pod, $"{id}/{id}.png", SessionFileState.Ready,
        SessionFilePreviewState.None, null, "owner", "user", DateTime.UnixEpoch,
        DateTime.UnixEpoch, null);

    private static byte[] WebpBytes()
    {
        var bytes = new byte[12];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        return bytes;
    }

    private static MemoryStream OfficePackage(string packageContentType)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("[Content_Types].xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write($"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Override PartName="/main.xml" ContentType="{packageContentType}" />
                </Types>
                """);
        }

        stream.Position = 0;
        return stream;
    }
}
