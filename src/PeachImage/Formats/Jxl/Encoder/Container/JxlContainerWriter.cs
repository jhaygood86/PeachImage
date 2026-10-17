using System.Buffers.Binary;

namespace PeachImage.Formats.Jxl.Encoder.Container;

/// <summary>
/// Wraps a codestream in the ISOBMFF container when there is metadata to carry, the inverse of
/// <c>JxlContainer.Parse</c>. Without metadata the bare codestream is written, which is the smaller and
/// equally valid form.
/// </summary>
internal static class JxlContainerWriter
{
    private static readonly byte[] SignatureBox = [0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];

    private static readonly byte[] FileTypeBox =
    [
        0x00, 0x00, 0x00, 0x14, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'j', (byte)'x', (byte)'l', (byte)' ', 0x00, 0x00, 0x00, 0x00,
        (byte)'j', (byte)'x', (byte)'l', (byte)' ',
    ];

    /// <summary>
    /// Writes <paramref name="codestream"/>, boxed together with <paramref name="exif"/> (TIFF header first), <paramref name="xmp"/> and
    /// the JPEG bitstream reconstruction data <paramref name="jbrd"/> when any of them is present.
    /// </summary>
    public static void Write(Stream stream, ReadOnlySpan<byte> codestream, byte[]? exif, byte[]? xmp, byte[]? jbrd = null)
    {
        bool hasExif = exif is { Length: > 0 };
        bool hasXmp = xmp is { Length: > 0 };
        if (!hasExif && !hasXmp && jbrd is null)
        {
            stream.Write(codestream);
            return;
        }

        stream.Write(SignatureBox);
        stream.Write(FileTypeBox);
        if (jbrd is not null)
        {
            WriteBoxHeader(stream, "jbrd", jbrd.Length);
            stream.Write(jbrd);
        }

        if (hasExif)
        {
            // The Exif box payload is a 4-byte offset to the TIFF header (0: it follows immediately).
            WriteBoxHeader(stream, "Exif", 4 + exif!.Length);
            stream.Write(new byte[4]);
            stream.Write(exif);
        }

        if (hasXmp)
        {
            WriteBoxHeader(stream, "xml ", xmp!.Length);
            stream.Write(xmp);
        }

        WriteBoxHeader(stream, "jxlc", codestream.Length);
        stream.Write(codestream);
    }

    private static void WriteBoxHeader(Stream stream, string type, long payloadLength)
    {
        long total = payloadLength + 8;
        if (total <= uint.MaxValue)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)total);
            WriteType(header[4..], type);
            stream.Write(header);
            return;
        }

        Span<byte> extended = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(extended, 1);
        WriteType(extended[4..], type);
        BinaryPrimitives.WriteUInt64BigEndian(extended[8..], (ulong)(payloadLength + 16));
        stream.Write(extended);
    }

    private static void WriteType(Span<byte> destination, string type)
    {
        for (int i = 0; i < 4; i++)
        {
            destination[i] = (byte)type[i];
        }
    }
}
