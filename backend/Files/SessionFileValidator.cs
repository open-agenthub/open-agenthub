using System.IO.Compression;
using System.Text;

namespace AgentHub.Api.Files;

public static class SessionFileValidator
{
    private const string DocxMime =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string PptxMime =
        "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly IReadOnlyDictionary<string, string> MimeByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".pdf"] = "application/pdf",
            [".md"] = "text/markdown",
            [".markdown"] = "text/markdown",
            [".txt"] = "text/plain",
            [".docx"] = DocxMime,
            [".pptx"] = PptxMime,
            [".xlsx"] = XlsxMime,
        };

    private static readonly HashSet<string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif",
    };

    public static SessionFileValidationResult ValidateReservation(
        SessionFileOptions options,
        string name,
        string mimeType,
        long size)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsSafeFileName(name))
        {
            return SessionFileValidationResult.Reject("unsupported_file_type");
        }

        var extension = Path.GetExtension(name);
        if (!MimeByExtension.TryGetValue(extension, out var expectedMime))
        {
            return SessionFileValidationResult.Reject("unsupported_file_type");
        }

        if (!string.Equals(expectedMime, mimeType?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return SessionFileValidationResult.Reject("content_type_mismatch");
        }

        var maximum = ImageMimeTypes.Contains(expectedMime)
            ? options.MaxImageBytes
            : options.MaxDocumentBytes;
        if (size < 0 || size > maximum)
        {
            return SessionFileValidationResult.Reject("file_too_large");
        }

        return SessionFileValidationResult.Accept();
    }

    public static SessionFileValidationResult ValidateBatch(
        SessionFileOptions options,
        IReadOnlyCollection<SessionFileRecord> files)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count > options.MaxMessageFiles)
        {
            return SessionFileValidationResult.Reject("attachment_count_exceeded");
        }

        long total = 0;
        foreach (var file in files)
        {
            if (file.Size > options.MaxMessageBytes - total)
            {
                return SessionFileValidationResult.Reject("attachment_bytes_exceeded");
            }

            total += file.Size;
        }

        return SessionFileValidationResult.Accept();
    }

    public static async Task<SessionFileValidationResult> DetectAsync(
        string name,
        Stream content,
        SessionFileOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        options ??= new SessionFileOptions();

        if (!IsSafeFileName(name) ||
            !MimeByExtension.TryGetValue(Path.GetExtension(name), out var expectedMime))
        {
            return SessionFileValidationResult.Reject("unsupported_file_type");
        }

        var bytes = await ReadBoundedAsync(content, options.MaxDocumentBytes, ct);
        if (bytes is null)
        {
            return SessionFileValidationResult.Reject("file_too_large");
        }

        var detectedMime = DetectMime(bytes, Path.GetExtension(name));
        if (detectedMime is null)
        {
            return SessionFileValidationResult.Reject("unsupported_file_type");
        }

        if (!string.Equals(expectedMime, detectedMime, StringComparison.OrdinalIgnoreCase))
        {
            return SessionFileValidationResult.Reject("content_type_mismatch");
        }

        return SessionFileValidationResult.Accept(detectedMime);
    }

    private static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 ||
            !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
        {
            return false;
        }

        return name.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) < 0 &&
            name.All(ch => !char.IsControl(ch));
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream content,
        long maximumBytes,
        CancellationToken ct)
    {
        if (maximumBytes > int.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var originalPosition = content.CanSeek ? content.Position : (long?)null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;

        try
        {
            while (true)
            {
                var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                total += read;
                if (total > maximumBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
        }
        finally
        {
            if (originalPosition.HasValue)
            {
                content.Position = originalPosition.Value;
            }
        }
    }

    private static string? DetectMime(byte[] bytes, string extension)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return "image/png";
        }

        if (span.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return "image/jpeg";
        }

        if (span.StartsWith("GIF87a"u8) || span.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (span.Length >= 12 && span[..4].SequenceEqual("RIFF"u8) &&
            span.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (span.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (span.StartsWith("PK\u0003\u0004"u8))
        {
            return DetectOfficeMime(bytes);
        }

        if (IsUtf8Text(bytes))
        {
            return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
                    ? "text/markdown"
                    : "text/plain";
        }

        return null;
    }

    private static string? DetectOfficeMime(byte[] bytes)
    {
        const int maxEntries = 4096;
        const long maxContentTypesBytes = 256 * 1024;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var contentTypes = archive.GetEntry("[Content_Types].xml");
            if (archive.Entries.Count > maxEntries || contentTypes is null ||
                contentTypes.Length > maxContentTypesBytes)
            {
                return null;
            }

            using var reader = new StreamReader(
                contentTypes.Open(), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxContentTypesBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = reader.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            if (total > maxContentTypesBytes) return null;
            var xml = new string(buffer, 0, total);
            if (xml.Contains("wordprocessingml.document.main+xml", StringComparison.Ordinal))
            {
                return DocxMime;
            }

            if (xml.Contains("presentationml.presentation.main+xml", StringComparison.Ordinal))
            {
                return PptxMime;
            }

            if (xml.Contains("spreadsheetml.sheet.main+xml", StringComparison.Ordinal))
            {
                return XlsxMime;
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return null;
    }

    private static bool IsUtf8Text(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Contains((byte)0))
        {
            return false;
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.All(ch => !char.IsControl(ch) || ch is '\r' or '\n' or '\t');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
