using System.Buffers.Binary;
using System.Text;

namespace AgentHub.Api.Files;

public sealed record VideoInspection(string? MimeType, string? Code, long Size)
{
    public bool Allowed => MimeType is not null;
}

/// <summary>
/// Content check for the two video containers session files accept (docs/session-file-video.md).
///
/// It walks the container instead of matching a magic number, because a signature alone says
/// nothing about whether the browser can play the file: an MP4 starts with the same <c>ftyp</c>
/// box whether it carries H.264 or HEVC, and a HEIC photo starts with one too. What gets
/// accepted is what the Files pane can actually show — H.264 video with AAC audio in MP4,
/// VP8/VP9/AV1 with Opus/Vorbis in WebM.
///
/// The stream is read once, front to back, and never buffered whole: a video is allowed to be
/// larger than any document, and holding it in memory to look at its first kilobytes is what
/// the document path does and what a 100 MiB upload must not. Only the MP4 <c>moov</c> box and
/// the WebM <c>Tracks</c> element are kept, each under a ceiling far above what a real file
/// needs. Every byte is counted, so the caller can compare the total with the reserved size.
/// </summary>
public static class VideoContainerInspector
{
    public const string Mp4 = "video/mp4";
    public const string WebM = "video/webm";

    // A moov for hours of video is a few MiB; the ceiling only stops a crafted file from
    // making us buffer the whole upload as "metadata".
    private const int MaxMetadataBytes = 16 * 1024 * 1024;
    private const int MaxHeaderBytes = 4096;

    private static readonly HashSet<string> Mp4Brands = new(StringComparer.Ordinal)
    {
        "isom", "iso2", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "dash", "M4V ",
    };

    private static readonly HashSet<string> WebMVideoCodecs = new(StringComparer.Ordinal)
    {
        "V_VP8", "V_VP9", "V_AV1",
    };

    private static readonly HashSet<string> WebMAudioCodecs = new(StringComparer.Ordinal)
    {
        "A_OPUS", "A_VORBIS",
    };

    public static bool IsVideoMime(string? mimeType) =>
        string.Equals(mimeType, Mp4, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mimeType, WebM, StringComparison.OrdinalIgnoreCase);

    public static async Task<VideoInspection> InspectAsync(
        Stream content,
        string expectedMime,
        long maximumBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var reader = new CountingReader(content, maximumBytes);
        try
        {
            var accepted = string.Equals(expectedMime, Mp4, StringComparison.OrdinalIgnoreCase)
                ? await InspectMp4Async(reader, ct)
                : string.Equals(expectedMime, WebM, StringComparison.OrdinalIgnoreCase)
                    ? await InspectWebMAsync(reader, ct)
                    : false;
            if (!accepted)
            {
                return new VideoInspection(null, "content_type_mismatch", reader.Position);
            }

            // The verdict covers the structure read so far; the rest still has to be counted so
            // a file with trailing bytes beyond its reservation does not pass.
            await reader.SkipToEndAsync(ct);
            return new VideoInspection(expectedMime.ToLowerInvariant(), null, reader.Position);
        }
        catch (VideoTooLargeException)
        {
            return new VideoInspection(null, "file_too_large", reader.Position);
        }
    }

    private static async Task<bool> InspectMp4Async(CountingReader reader, CancellationToken ct)
    {
        var header = new byte[16];
        var first = true;
        byte[]? moov = null;
        while (true)
        {
            var read = await reader.ReadAsync(header.AsMemory(0, 8), ct);
            if (read == 0 && !first) break;
            if (read != 8) return false;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            long headerLength = 8;
            if (size == 1)
            {
                if (await reader.ReadAsync(header.AsMemory(8, 8), ct) != 8) return false;
                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)), long.MaxValue);
                headerLength = 16;
            }

            if (first)
            {
                if (type != "ftyp" || size < 16 || size > MaxHeaderBytes) return false;
                var ftyp = await reader.ReadExactlyAsync((int)(size - headerLength), ct);
                if (ftyp is null || !HasMp4Brand(ftyp)) return false;
                first = false;
                continue;
            }

            if (size == 0)
            {
                // "Extends to the end of the file" — legal only for the last box, which is where
                // muxers that cannot seek back put mdat. Nothing after it can be a moov.
                if (type == "moov") return false;
                break;
            }

            if (size < headerLength) return false;
            var payload = size - headerLength;
            if (type == "moov")
            {
                if (moov is not null || payload > MaxMetadataBytes) return false;
                moov = await reader.ReadExactlyAsync((int)payload, ct);
                if (moov is null) return false;
            }
            else if (!await reader.SkipAsync(payload, ct))
            {
                return false;
            }
        }

        return moov is not null && Mp4TracksPlayable(moov);
    }

    private static bool HasMp4Brand(byte[] ftyp)
    {
        if (ftyp.Length < 8) return false;
        if (Mp4Brands.Contains(Encoding.ASCII.GetString(ftyp, 0, 4))) return true;
        for (var offset = 8; offset + 4 <= ftyp.Length; offset += 4)
        {
            if (Mp4Brands.Contains(Encoding.ASCII.GetString(ftyp, offset, 4))) return true;
        }

        return false;
    }

    private static bool Mp4TracksPlayable(byte[] moov)
    {
        var videoTracks = 0;
        foreach (var trak in Boxes(moov, 0, moov.Length).Where(box => box.Type == "trak"))
        {
            var mdia = Child(moov, trak, "mdia");
            var hdlr = mdia is null ? null : Child(moov, mdia.Value, "hdlr");
            var stsd = mdia is null ? null
                : Path(moov, mdia.Value, "minf", "stbl", "stsd");
            if (hdlr is null || hdlr.Value.Length < 12 || stsd is null || stsd.Value.Length < 8)
            {
                return false;
            }

            var handler = Encoding.ASCII.GetString(moov, hdlr.Value.Start + 8, 4);
            var formats = Boxes(moov, stsd.Value.Start + 8, stsd.Value.Length - 8)
                .Select(entry => entry.Type).ToArray();
            if (formats.Length == 0) return false;
            switch (handler)
            {
                case "vide":
                    if (!formats.All(format => format is "avc1" or "avc3")) return false;
                    videoTracks++;
                    break;
                case "soun":
                    if (!formats.All(format => format == "mp4a")) return false;
                    break;
                // Timecode, chapter or metadata tracks are not played, so they cannot make the
                // file unplayable; refusing them would refuse ordinary editor exports.
            }
        }

        return videoTracks > 0;
    }

    private readonly record struct Box(string Type, int Start, int Length);

    private static List<Box> Boxes(byte[] data, int start, int length)
    {
        var boxes = new List<Box>();
        var offset = start;
        var end = start + length;
        while (offset + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var headerLength = 8;
            if (size == 1)
            {
                if (offset + 16 > end) break;
                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8)), long.MaxValue);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - offset;
            }

            if (size < headerLength || size > end - offset) break;
            boxes.Add(new Box(type, offset + headerLength, (int)size - headerLength));
            offset += (int)size;
        }

        return boxes;
    }

    private static Box? Child(byte[] data, Box parent, string type) =>
        Boxes(data, parent.Start, parent.Length).Where(box => box.Type == type)
            .Select(box => (Box?)box).FirstOrDefault();

    private static Box? Path(byte[] data, Box parent, params string[] types)
    {
        Box? current = parent;
        foreach (var type in types)
        {
            current = current is null ? null : Child(data, current.Value, type);
        }

        return current;
    }

    private const uint EbmlId = 0x1A45DFA3;
    private const uint DocTypeId = 0x4282;
    private const uint SegmentId = 0x18538067;
    private const uint TracksId = 0x1654AE6B;
    private const uint ClusterId = 0x1F43B675;
    private const uint TrackEntryId = 0xAE;
    private const uint TrackTypeId = 0x83;
    private const uint CodecId = 0x86;

    private static async Task<bool> InspectWebMAsync(CountingReader reader, CancellationToken ct)
    {
        var ebml = await ReadElementHeaderAsync(reader, ct);
        if (ebml is not { Id: EbmlId, Size: > 0 and <= MaxHeaderBytes }) return false;
        var header = await reader.ReadExactlyAsync((int)ebml.Value.Size!, ct);
        if (header is null) return false;
        var docType = Elements(header).Where(element => element.Id == DocTypeId)
            .Select(element => Encoding.ASCII.GetString(header, element.Start, element.Length).TrimEnd('\0'))
            .FirstOrDefault();
        if (docType != "webm") return false;

        var segment = await ReadElementHeaderAsync(reader, ct);
        if (segment is not { Id: SegmentId }) return false;

        // Tracks precede the first Cluster in every muxer we care about, including the
        // MediaRecorder output whose Segment and Clusters have no size at all.
        while (true)
        {
            var child = await ReadElementHeaderAsync(reader, ct);
            if (child is null || child.Value.Id == ClusterId || child.Value.Size is null) return false;
            if (child.Value.Id == TracksId)
            {
                if (child.Value.Size > MaxMetadataBytes) return false;
                var tracks = await reader.ReadExactlyAsync((int)child.Value.Size, ct);
                return tracks is not null && WebMTracksPlayable(tracks);
            }

            if (!await reader.SkipAsync(child.Value.Size.Value, ct)) return false;
        }
    }

    private static bool WebMTracksPlayable(byte[] tracks)
    {
        var videoTracks = 0;
        foreach (var entry in Elements(tracks).Where(element => element.Id == TrackEntryId))
        {
            var fields = Elements(tracks, entry.Start, entry.Length);
            var trackType = fields.Where(field => field.Id == TrackTypeId && field.Length is > 0 and <= 8)
                .Select(field => ReadUnsigned(tracks, field)).FirstOrDefault();
            var codec = fields.Where(field => field.Id == CodecId)
                .Select(field => Encoding.ASCII.GetString(tracks, field.Start, field.Length).TrimEnd('\0'))
                .FirstOrDefault();
            switch (trackType)
            {
                case 1:
                    if (codec is null || !WebMVideoCodecs.Contains(codec)) return false;
                    videoTracks++;
                    break;
                case 2:
                    if (codec is null || !WebMAudioCodecs.Contains(codec)) return false;
                    break;
            }
        }

        return videoTracks > 0;
    }

    private readonly record struct ElementHeader(uint Id, long? Size);
    private readonly record struct Element(uint Id, int Start, int Length);

    private static async Task<ElementHeader?> ReadElementHeaderAsync(
        CountingReader reader,
        CancellationToken ct)
    {
        var id = await ReadVintAsync(reader, keepMarker: true, maxLength: 4, ct);
        if (id is null) return null;
        var size = await ReadVintAsync(reader, keepMarker: false, maxLength: 8, ct);
        if (size is null) return null;
        return new ElementHeader((uint)id.Value.Value,
            size.Value.AllOnes ? null : size.Value.Value);
    }

    private static async Task<(long Value, bool AllOnes)?> ReadVintAsync(
        CountingReader reader,
        bool keepMarker,
        int maxLength,
        CancellationToken ct)
    {
        var first = new byte[1];
        if (await reader.ReadAsync(first, ct) != 1) return null;
        var length = System.Numerics.BitOperations.LeadingZeroCount((uint)first[0]) - 23;
        if (first[0] == 0 || length > maxLength) return null;
        var rest = length == 1 ? Array.Empty<byte>() : await reader.ReadExactlyAsync(length - 1, ct);
        if (rest is null) return null;
        var bytes = new byte[length];
        bytes[0] = first[0];
        rest.CopyTo(bytes, 1);
        return Decode(bytes, keepMarker);
    }

    private static (long Value, bool AllOnes) Decode(ReadOnlySpan<byte> bytes, bool keepMarker)
    {
        long value = keepMarker ? bytes[0] : bytes[0] & (0xFF >> bytes.Length);
        var allOnes = value == (0xFF >> bytes.Length);
        for (var i = 1; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
            allOnes &= bytes[i] == 0xFF;
        }

        return (value, allOnes);
    }

    private static List<Element> Elements(byte[] data) => Elements(data, 0, data.Length);

    private static List<Element> Elements(byte[] data, int start, int length)
    {
        var elements = new List<Element>();
        var offset = start;
        var end = start + length;
        while (offset < end)
        {
            var idLength = VintLength(data[offset]);
            if (idLength is 0 or > 4 || offset + idLength >= end) break;
            var id = (uint)Decode(data.AsSpan(offset, idLength), keepMarker: true).Value;
            offset += idLength;
            var sizeLength = VintLength(data[offset]);
            if (sizeLength == 0 || offset + sizeLength > end) break;
            var (size, allOnes) = Decode(data.AsSpan(offset, sizeLength), keepMarker: false);
            offset += sizeLength;
            if (allOnes || size > end - offset) break;
            elements.Add(new Element(id, offset, (int)size));
            offset += (int)size;
        }

        return elements;
    }

    private static int VintLength(byte first) =>
        first == 0 ? 0 : System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;

    private static long ReadUnsigned(byte[] data, Element element)
    {
        long value = 0;
        for (var i = 0; i < element.Length; i++)
        {
            value = (value << 8) | data[element.Start + i];
        }

        return value;
    }

    private sealed class VideoTooLargeException : Exception;

    private sealed class CountingReader(Stream inner, long maximumBytes)
    {
        private readonly byte[] _discard = new byte[81920];
        public long Position { get; private set; }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await inner.ReadAsync(buffer[total..], ct);
                if (read == 0) break;
                total += read;
                Count(read);
            }

            return total;
        }

        public async Task<byte[]?> ReadExactlyAsync(int count, CancellationToken ct)
        {
            if (count < 0) return null;
            if (count > maximumBytes - Position) throw new VideoTooLargeException();
            var buffer = new byte[count];
            return await ReadAsync(buffer, ct) == count ? buffer : null;
        }

        public async Task<bool> SkipAsync(long count, CancellationToken ct)
        {
            if (count > maximumBytes - Position) throw new VideoTooLargeException();
            while (count > 0)
            {
                var read = await inner.ReadAsync(
                    _discard.AsMemory(0, (int)Math.Min(_discard.Length, count)), ct);
                if (read == 0) return false;
                Count(read);
                count -= read;
            }

            return true;
        }

        public async Task SkipToEndAsync(CancellationToken ct)
        {
            while (true)
            {
                var read = await inner.ReadAsync(_discard, ct);
                if (read == 0) return;
                Count(read);
            }
        }

        private void Count(int read)
        {
            Position += read;
            if (Position > maximumBytes) throw new VideoTooLargeException();
        }
    }
}
