using System.Buffers.Binary;
using System.Text;
using AgentHub.Api.Files;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Xunit;
using FileContentResult = AgentHub.Api.Files.FileContentResult;

namespace AgentHub.Api.Tests;

public sealed class SessionFileVideoTests
{
    [Fact]
    public async Task An_h264_aac_mp4_is_accepted_by_content()
    {
        var result = await Detect("demo.mp4", VideoFixtures.Mp4);

        Assert.True(result.Allowed, result.Code);
        Assert.Equal("video/mp4", result.DetectedMimeType);
    }

    [Fact]
    public async Task A_vp9_opus_webm_is_accepted_by_content()
    {
        var result = await Detect("demo.webm", VideoFixtures.WebM);

        Assert.True(result.Allowed, result.Code);
        Assert.Equal("video/webm", result.DetectedMimeType);
    }

    [Fact]
    public async Task An_mp4_whose_moov_follows_mdat_is_accepted()
    {
        // What a muxer that cannot seek back writes, and what ffmpeg writes without faststart.
        // The inspector has to read past the media data to find the track list.
        var reordered = MoveMoovToEnd(VideoFixtures.Mp4);
        Assert.NotEqual(VideoFixtures.Mp4, reordered);

        var result = await Detect("demo.mp4", reordered);

        Assert.True(result.Allowed, result.Code);
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("matroska-as-webm")]
    [InlineData("webm-as-mp4")]
    [InlineData("mp4-as-webm")]
    [InlineData("text-as-mp4")]
    [InlineData("png-as-mp4")]
    [InlineData("truncated")]
    [InlineData("no-moov")]
    public async Task A_file_that_is_not_a_playable_video_of_its_type_is_refused(string variant)
    {
        var (name, bytes) = variant switch
        {
            // Same ftyp box as the accepted file; only the sample entry says hvc1, which the
            // Files pane cannot be relied on to play.
            "hevc" => ("demo.mp4", VideoFixtures.HevcMp4),
            "matroska-as-webm" => ("demo.webm", VideoFixtures.Matroska),
            "webm-as-mp4" => ("demo.mp4", VideoFixtures.WebM),
            "mp4-as-webm" => ("demo.webm", VideoFixtures.Mp4),
            "text-as-mp4" => ("demo.mp4", Encoding.UTF8.GetBytes("not a video, just renamed\n")),
            "png-as-mp4" => ("demo.mp4", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "truncated" => ("demo.mp4", VideoFixtures.Mp4[..^100]),
            "no-moov" => ("demo.mp4", WithoutBox(VideoFixtures.Mp4, "moov")),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var result = await Detect(name, bytes);

        Assert.False(result.Allowed);
        Assert.Equal("content_type_mismatch", result.Code);
    }

    [Fact]
    public async Task Inspection_counts_every_byte_and_stops_past_the_limit()
    {
        var video = VideoFixtures.Mp4;
        await using var whole = new MemoryStream(video);
        await using var again = new MemoryStream(video);
        var counted = await VideoContainerInspector.InspectAsync(whole, "video/mp4", video.Length);
        var over = await VideoContainerInspector.InspectAsync(again, "video/mp4", video.Length - 1);

        Assert.Equal(video.Length, counted.Size);
        Assert.Equal("file_too_large", over.Code);
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4", 100L * 1024 * 1024, true)]
    [InlineData("clip.mp4", "video/mp4", 100L * 1024 * 1024 + 1, false)]
    [InlineData("clip.webm", "video/webm", 60L * 1024 * 1024, true)]
    [InlineData("clip.mp4", "video/webm", 1024, false)]
    [InlineData("clip.mov", "video/quicktime", 1024, false)]
    public void Videos_have_their_own_reservation_limit(string name, string mime, long size, bool allowed)
    {
        var result = SessionFileValidator.ValidateReservation(new SessionFileOptions(), name, mime, size);

        Assert.Equal(allowed, result.Allowed);
    }

    [Fact]
    public void Without_object_storage_a_video_is_held_to_the_document_limit()
    {
        var options = new SessionFileOptions();
        var size = options.MaxDocumentBytes + 1;

        Assert.True(SessionFileValidator.ValidateReservation(
            options, "clip.mp4", "video/mp4", size, SessionFileStorageKind.S3).Allowed);
        Assert.Equal("file_too_large", SessionFileValidator.ValidateReservation(
            options, "clip.mp4", "video/mp4", size, SessionFileStorageKind.Pod).Code);
    }

    [Fact]
    public void The_video_limit_does_not_move_the_image_limit()
    {
        var options = new SessionFileOptions { MaxVideoBytes = 500L * 1024 * 1024 };

        Assert.Equal("file_too_large", SessionFileValidator.ValidateReservation(
            options, "shot.png", "image/png", options.MaxImageBytes + 1).Code);
        Assert.Equal(options.MaxVideoBytes, SessionFileValidator.MaxUploadBytes(options));
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 100)]
    [InlineData("bytes=900-", 1000, 900, 100)]
    [InlineData("bytes=900-5000", 1000, 900, 100)]
    [InlineData("bytes=-10", 1000, 990, 10)]
    [InlineData("bytes=-5000", 1000, 0, 1000)]
    public void A_single_range_is_resolved_against_the_file_size(
        string header, long size, long from, long length)
    {
        var range = ByteRangeRequest.Parse(header)!.Resolve(size);

        Assert.Equal(new ByteRange(from, length), range);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-1")]
    [InlineData("bytes=5-1")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("bytes=-")]
    [InlineData("bytes=a-b")]
    public void Anything_but_one_byte_range_means_the_whole_file(string? header)
    {
        Assert.Null(ByteRangeRequest.Parse(header));
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=0-", 0)]
    public void A_range_past_the_end_is_not_satisfiable(string header, long size)
    {
        Assert.Null(ByteRangeRequest.Parse(header)!.Resolve(size));
    }

    [Fact]
    public async Task A_sliced_stream_returns_exactly_the_range()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        await using var slice = new SlicedReadStream(new MemoryStream(bytes), 100_000, 5);
        using var output = new MemoryStream();

        await slice.CopyToAsync(output);

        Assert.Equal(bytes.AsSpan(100_000, 5).ToArray(), output.ToArray());
    }

    [Fact]
    public async Task A_range_is_answered_with_206_and_its_content_range()
    {
        var context = await Execute(new FileContentResult(
            new MemoryStream([5, 6, 7]), null, "video/mp4", "demo.mp4", 10, new ByteRange(5, 3)));

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 5-7/10", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(3, context.Response.ContentLength);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("video/mp4", context.Response.ContentType);
        Assert.StartsWith("inline", context.Response.Headers.ContentDisposition.ToString());
        Assert.Equal([5, 6, 7], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public void A_full_read_advertises_ranges()
    {
        using var body = new MemoryStream([1, 2, 3, 4]);
        var (context, result) = Respond(new FileContentResult(body, null, "video/webm", "demo.webm", 4));

        Assert.IsType<FileStreamResult>(result);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public void An_unsatisfiable_range_is_answered_with_416()
    {
        var (context, result) = Respond(new FileContentResult(null, null, "video/mp4", "demo.mp4", 10));

        Assert.Equal(416, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal("bytes */10", context.Response.Headers.ContentRange.ToString());
    }

    private static async Task<SessionFileValidationResult> Detect(string name, byte[] bytes)
    {
        await using var content = new MemoryStream(bytes);
        return await SessionFileValidator.DetectAsync(name, content, new SessionFileOptions());
    }

    private static (DefaultHttpContext Context, IActionResult Result) Respond(FileContentResult content)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var controller = new ProbeController
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        return (context, SessionFileApi.Content(controller, content));
    }

    private static async Task<DefaultHttpContext> Execute(FileContentResult content)
    {
        var (context, result) = Respond(content);
        await result.ExecuteResultAsync(new ActionContext(
            context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return context;
    }

    private sealed class ProbeController : ControllerBase;

    private static List<(string Type, byte[] Bytes)> TopLevelBoxes(byte[] file)
    {
        var boxes = new List<(string, byte[])>();
        var offset = 0;
        while (offset < file.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
            boxes.Add((Encoding.ASCII.GetString(file, offset + 4, 4), file[offset..(offset + size)]));
            offset += size;
        }

        return boxes;
    }

    private static byte[] MoveMoovToEnd(byte[] file)
    {
        var boxes = TopLevelBoxes(file);
        return boxes.Where(box => box.Type != "moov")
            .Concat(boxes.Where(box => box.Type == "moov"))
            .SelectMany(box => box.Bytes).ToArray();
    }

    private static byte[] WithoutBox(byte[] file, string type) =>
        TopLevelBoxes(file).Where(box => box.Type != type).SelectMany(box => box.Bytes).ToArray();
}
