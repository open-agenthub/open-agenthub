using System.Security.Cryptography;
using System.Text;

namespace AgentHub.Api.Files;

/// <summary>A single byte range resolved against a file's size: <c>Length</c> bytes from <c>From</c>.</summary>
public sealed record ByteRange(long From, long Length)
{
    public long To => From + Length - 1;
}

/// <summary>
/// A request's <c>Range</c> header, before the file's size is known.
///
/// Only one <c>bytes</c> range is honoured. A media element asks for exactly one, and
/// answering a multi-range or malformed header with the whole file is what RFC 9110 allows —
/// cheaper and safer than a multipart/byteranges body nobody here needs.
/// </summary>
public sealed record ByteRangeRequest(long? From, long? To, long? Suffix)
{
    public static ByteRangeRequest? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = value["bytes=".Length..].Trim();
        if (spec.Contains(',')) return null;
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            return long.TryParse(last, System.Globalization.NumberStyles.None, null, out var suffix)
                ? new ByteRangeRequest(null, null, suffix)
                : null;
        }

        if (!long.TryParse(first, System.Globalization.NumberStyles.None, null, out var from)) return null;
        if (last.Length == 0) return new ByteRangeRequest(from, null, null);
        if (!long.TryParse(last, System.Globalization.NumberStyles.None, null, out var to) || to < from)
        {
            return null;
        }

        return new ByteRangeRequest(from, to, null);
    }

    /// <summary>The bytes this asks for out of <paramref name="size"/>, or null when none of them exist.</summary>
    public ByteRange? Resolve(long size)
    {
        if (Suffix is { } suffix)
        {
            if (suffix == 0 || size == 0) return null;
            var length = Math.Min(suffix, size);
            return new ByteRange(size - length, length);
        }

        var from = From!.Value;
        if (from >= size) return null;
        var to = Math.Min(To ?? size - 1, size - 1);
        return new ByteRange(from, to - from + 1);
    }
}

/// <summary>
/// Reads <c>length</c> bytes of <c>inner</c> after skipping <c>skip</c> of them. The fallback for
/// a source that answered a range request with the whole content — object storage without
/// range support, or a session pod still running a runtime from before ranges.
/// </summary>
public sealed class SlicedReadStream(Stream inner, long skip, long length) : Stream
{
    private readonly long _length = length;
    private long _skip = skip;
    private long _remaining = length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_skip > 0)
        {
            var discard = new byte[(int)Math.Min(81920, _skip)];
            var skipped = await inner.ReadAsync(discard, ct);
            if (skipped == 0) return 0;
            _skip -= skipped;
        }

        if (_remaining <= 0) return 0;
        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

public sealed record SessionFileStreamTicket(
    string Hash,
    string SessionId,
    string FileId,
    string Principal,
    DateTime ExpiresAt);

public sealed record IssuedStreamTicket(string Url, DateTime ExpiresAt);

/// <summary>
/// Short-lived, single-file read grants for the Files pane's &lt;video&gt; element.
///
/// A media element fetches its source itself, in ranges, and cannot attach the bearer header
/// every other file read carries. Fetching the whole video into a blob first works but defeats
/// seeking and holds up to MaxVideoBytes in the tab. The alternatives were the access token in
/// the query string — the same credential the WebSocket uses, but leaked into every proxy log
/// and the media cache for its whole lifetime and good for every API call — or a Data
/// Protection token, whose keys are per replica here and would fail on the other backend pod.
/// A ticket is a random value whose hash is stored in Postgres: it names one file, expires,
/// and is checked against the holder's current access on every request, so revoking a share
/// still ends playback.
/// </summary>
public sealed class SessionFileStreamTickets(ISessionFileRegistry registry, SessionFileOptions options)
{
    public async Task<IssuedStreamTicket> IssueAsync(
        SessionFileActor actor,
        string fileId,
        CancellationToken ct = default)
    {
        var file = await registry.GetAsync(actor.SessionId, fileId, ct)
            ?? throw new SessionFileException("file_not_found");
        if (file.State != SessionFileState.Ready)
        {
            throw new SessionFileException("file_not_ready");
        }

        // Only what a media element plays. Any other type is read through the bearer route,
        // and a ticket for it would be a second, header-free way to fetch it.
        if (!VideoContainerInspector.IsVideoMime(file.DetectedMimeType ?? file.DeclaredMimeType))
        {
            throw new SessionFileException("stream_unsupported");
        }

        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTime.UtcNow.AddMinutes(options.StreamTicketMinutes);
        await registry.InsertStreamTicketAsync(new SessionFileStreamTicket(
            Hash(ticket), actor.SessionId, file.Id, actor.Principal, expiresAt), ct);
        return new IssuedStreamTicket(
            $"/api/sessions/{Uri.EscapeDataString(actor.SessionId)}/files/" +
            $"{Uri.EscapeDataString(file.Id)}/stream?ticket={ticket}",
            expiresAt);
    }

    /// <summary>The principal the ticket was issued to, or null when it does not grant this file now.</summary>
    public async Task<string?> RedeemAsync(
        string? ticket,
        string sessionId,
        string fileId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 128) return null;
        var stored = await registry.GetStreamTicketAsync(Hash(ticket), ct);
        return stored is not null && stored.ExpiresAt > DateTime.UtcNow &&
            string.Equals(stored.SessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(stored.FileId, fileId, StringComparison.Ordinal)
                ? stored.Principal
                : null;
    }

    private static string Hash(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
