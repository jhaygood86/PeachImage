namespace PeachImage.Formats.Avif.Container;

/// <summary>
/// Low-level big-endian ISOBMFF box-composition helpers -- the write-side counterpart to
/// <see cref="AvifBinaryReader"/>/<see cref="AvifBoxReader"/>. Boxes are built bottom-up into <c>byte[]</c>
/// (a child's bytes are ready before its parent wraps them) rather than streamed with backpatching, since
/// ISOBMFF's length-prefixed nesting makes two-pass sizing simpler than backpatching a stream, and a single
/// AVIF still image's <c>meta</c> box is only a few hundred bytes total.
/// </summary>
internal static class AvifBoxWriter
{
    /// <summary>Wraps <paramref name="payload"/> in a plain (non-FullBox) box: <c>size(4) + fourCC(4) + payload</c>.</summary>
    public static byte[] Box(string fourCc, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        WriteUInt32(result, 0, (uint)result.Length);
        WriteFourCc(result, 4, fourCc);
        Array.Copy(payload, 0, result, 8, payload.Length);
        return result;
    }

    /// <summary>Wraps the concatenation of <paramref name="children"/> (already-built child boxes) in a box.</summary>
    public static byte[] Box(string fourCc, params byte[][] children) => Box(fourCc, Concat(children));

    /// <summary>Wraps <paramref name="payload"/> in a FullBox: <c>size(4) + fourCC(4) + version(1) + flags(3) + payload</c>.</summary>
    public static byte[] FullBox(string fourCc, byte version, uint flags, byte[] payload)
    {
        var full = new byte[4 + payload.Length];
        full[0] = version;
        full[1] = (byte)(flags >> 16);
        full[2] = (byte)(flags >> 8);
        full[3] = (byte)flags;
        Array.Copy(payload, 0, full, 4, payload.Length);
        return Box(fourCc, full);
    }

    public static byte[] Concat(ReadOnlySpan<byte[]> chunks)
    {
        int total = 0;
        foreach (var chunk in chunks)
        {
            total += chunk.Length;
        }

        var result = new byte[total];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }

    public static void WriteUInt8(byte[] buffer, int offset, byte value) => buffer[offset] = value;

    public static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    public static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    public static void WriteFourCc(byte[] buffer, int offset, string fourCc)
    {
        for (int i = 0; i < 4; i++)
        {
            buffer[offset + i] = (byte)fourCc[i];
        }
    }

    public static byte[] FourCcBytes(string fourCc)
    {
        var bytes = new byte[4];
        WriteFourCc(bytes, 0, fourCc);
        return bytes;
    }
}
