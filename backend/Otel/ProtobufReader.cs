using System.Text;

namespace AgentHub.Api.Otel;

/// <summary>
/// Minimal, dependency-free reader for the protobuf wire format
/// (https://protobuf.dev/programming-guides/encoding/). Only the pieces needed
/// to walk an OTLP/HTTP metrics payload are implemented. Operating on a span keeps
/// it allocation-light; length-delimited fields return sub-readers so the caller can
/// recurse into nested messages without copying.
/// </summary>
internal ref struct ProtobufReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtobufReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public bool End => _pos >= _data.Length;

    // Wire types we care about.
    public const int WireVarint = 0;
    public const int WireI64 = 1;
    public const int WireLen = 2;
    public const int WireI32 = 5;

    /// <summary>Reads the next field tag. Returns false at the end of the buffer.</summary>
    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        if (End) { fieldNumber = 0; wireType = 0; return false; }
        var tag = ReadVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _data.Length) throw new FormatException("Truncated varint.");
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new FormatException("Varint too long.");
        }
        return result;
    }

    public ulong ReadFixed64()
    {
        if (_pos + 8 > _data.Length) throw new FormatException("Truncated fixed64.");
        ulong v = 0;
        for (int i = 0; i < 8; i++) v |= (ulong)_data[_pos + i] << (8 * i);
        _pos += 8;
        return v;
    }

    public uint ReadFixed32()
    {
        if (_pos + 4 > _data.Length) throw new FormatException("Truncated fixed32.");
        uint v = 0;
        for (int i = 0; i < 4; i++) v |= (uint)_data[_pos + i] << (8 * i);
        _pos += 4;
        return v;
    }

    public double ReadDouble() => BitConverter.Int64BitsToDouble((long)ReadFixed64());

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        int len = checked((int)ReadVarint());
        if (_pos + len > _data.Length) throw new FormatException("Truncated length-delimited field.");
        var slice = _data.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

    /// <summary>Skips a field of the given wire type (used for fields we do not consume).</summary>
    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case WireVarint: ReadVarint(); break;
            case WireI64: _pos += 8; break;
            case WireLen: ReadLengthDelimited(); break;
            case WireI32: _pos += 4; break;
            default: throw new FormatException($"Unsupported wire type {wireType}.");
        }
        if (_pos > _data.Length) throw new FormatException("Skipped past end of buffer.");
    }
}
