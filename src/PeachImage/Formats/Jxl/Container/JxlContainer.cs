using System.Buffers.Binary;
using System.IO.Compression;
using PeachImage.Formats.Jxl.Internal;

namespace PeachImage.Formats.Jxl.Container;

/// <summary>
/// The result of unwrapping a JPEG XL file: either a bare codestream (<c>FF 0A</c>) or an ISOBMFF container
/// whose <c>jxlc</c>/<c>jxlp</c> boxes carry the codestream and whose <c>Exif</c>/<c>xml </c> boxes (optionally
/// Brotli-compressed in a <c>brob</c> box) carry metadata.
/// </summary>
internal sealed class JxlContainer
{
    private static readonly byte[] ContainerSignature = [0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];

    private JxlContainer(ReadOnlyMemory<byte> codestream, byte[]? exif, byte[]? xmp, byte[]? exifBox = null, byte[]? jpegReconstruction = null)
    {
        Codestream = codestream;
        Exif = exif;
        Xmp = xmp;
        ExifBox = exifBox;
        JpegReconstruction = jpegReconstruction;
    }

    /// <summary>The raw codestream, beginning with its <c>FF 0A</c> signature. May alias the input buffer.</summary>
    public ReadOnlyMemory<byte> Codestream { get; }

    /// <summary>The Exif payload starting at its TIFF header, if present.</summary>
    public byte[]? Exif { get; }

    /// <summary>The XMP packet, if present.</summary>
    public byte[]? Xmp { get; }

    /// <summary>The complete content of the Exif box (its 4-byte TIFF offset included), as JPEG reconstruction needs it.</summary>
    public byte[]? ExifBox { get; }

    /// <summary>The content of the <c>jbrd</c> box (JPEG bitstream reconstruction data), if the file was made from a JPEG.</summary>
    public byte[]? JpegReconstruction { get; }

    /// <summary>Whether <paramref name="header"/> begins with a bare-codestream or ISOBMFF-container signature.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> header) =>
        (header.Length >= 2 && header[0] == 0xFF && header[1] == 0x0A) ||
        (header.Length >= ContainerSignature.Length && header[..ContainerSignature.Length].SequenceEqual(ContainerSignature));

    /// <summary>Unwraps <paramref name="file"/>, which holds the entire JPEG XL file.</summary>
    public static JxlContainer Parse(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length >= 2 && file[0] == 0xFF && file[1] == 0x0A)
        {
            return new JxlContainer(file, exif: null, xmp: null);
        }

        if (!HasSignature(file))
        {
            throw new JxlDecodingException("The data is not a JPEG XL codestream or container.");
        }

        return ParseBoxes(file);
    }

    private static JxlContainer ParseBoxes(byte[] file)
    {
        ReadOnlyMemory<byte> singleCodestream = default;
        bool haveJxlc = false;
        List<(int Offset, int Length)>? partial = null;
        bool sawLastPartial = false;
        uint nextPartialIndex = 0;
        byte[]? exif = null;
        byte[]? xmp = null;
        byte[]? exifBox = null;
        byte[]? jbrd = null;

        long position = 0;
        bool first = true;
        while (position < file.Length)
        {
            if (file.Length - position < 8)
            {
                throw new JxlDecodingException("Truncated box header in the JPEG XL container.");
            }

            int at = (int)position;
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at, 4));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 4, 4));
            int headerSize = 8;
            if (size == 1)
            {
                if (file.Length - position < 16)
                {
                    throw new JxlDecodingException("Truncated extended box header in the JPEG XL container.");
                }

                size = BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(at + 8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(file.Length - position);
            }

            if (size < (ulong)headerSize || size > (ulong)(file.Length - position))
            {
                throw new JxlDecodingException("A box in the JPEG XL container has an invalid size.");
            }

            int payloadOffset = at + headerSize;
            int payloadLength = (int)size - headerSize;
            position += (long)size;

            if (first)
            {
                if (type != Fourcc("JXL ") || payloadLength != 4)
                {
                    throw new JxlDecodingException("The JPEG XL container does not begin with a signature box.");
                }

                first = false;
                continue;
            }

            if (type == Fourcc("jxlc"))
            {
                if (haveJxlc || partial is not null)
                {
                    throw new JxlDecodingException("The JPEG XL container has more than one codestream box.");
                }

                haveJxlc = true;
                singleCodestream = file.AsMemory(payloadOffset, payloadLength);
            }
            else if (type == Fourcc("jxlp"))
            {
                if (haveJxlc || sawLastPartial)
                {
                    throw new JxlDecodingException("The JPEG XL container mixes or over-runs its codestream boxes.");
                }

                if (payloadLength < 4)
                {
                    throw new JxlDecodingException("A partial codestream box is too short.");
                }

                uint indexField = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(payloadOffset, 4));
                if ((indexField & 0x7FFFFFFF) != nextPartialIndex)
                {
                    throw new JxlDecodingException("Partial codestream boxes are out of order.");
                }

                nextPartialIndex++;
                sawLastPartial = (indexField & 0x80000000) != 0;
                (partial ??= []).Add((payloadOffset + 4, payloadLength - 4));
            }
            else if (type == Fourcc("jbrd"))
            {
                jbrd ??= file.AsSpan(payloadOffset, payloadLength).ToArray();
            }
            else if (type == Fourcc("Exif"))
            {
                exif ??= ExtractExif(file.AsSpan(payloadOffset, payloadLength));
                exifBox ??= file.AsSpan(payloadOffset, payloadLength).ToArray();
            }
            else if (type == Fourcc("xml "))
            {
                xmp ??= file.AsSpan(payloadOffset, payloadLength).ToArray();
            }
            else if (type == Fourcc("brob") && payloadLength >= 4)
            {
                uint innerType = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(payloadOffset, 4));
                if (innerType == Fourcc("Exif") && exif is null)
                {
                    exifBox = Decompress(file.AsSpan(payloadOffset + 4, payloadLength - 4));
                    exif = ExtractExif(exifBox);
                }
                else if (innerType == Fourcc("xml ") && xmp is null)
                {
                    xmp = Decompress(file.AsSpan(payloadOffset + 4, payloadLength - 4));
                }
            }
        }

        if (haveJxlc)
        {
            return new JxlContainer(singleCodestream, exif, xmp, exifBox, jbrd);
        }

        if (partial is null || !sawLastPartial)
        {
            throw new JxlDecodingException("The JPEG XL container has no complete codestream.");
        }

        long total = 0;
        foreach (var (_, length) in partial)
        {
            total += length;
        }

        if (total > JxlDecodingLimits.MaxFileSize)
        {
            throw new JxlDecodingException("The JPEG XL codestream is too large.");
        }

        var joined = new byte[total];
        int written = 0;
        foreach (var (offset, length) in partial)
        {
            file.AsSpan(offset, length).CopyTo(joined.AsSpan(written));
            written += length;
        }

        return new JxlContainer(joined, exif, xmp, exifBox, jbrd);
    }

    // An Exif box starts with a big-endian offset to the TIFF header (skipping any "Exif\0\0" prefix).
    private static byte[]? ExtractExif(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return null;
        }

        uint tiffOffset = BinaryPrimitives.ReadUInt32BigEndian(payload);
        if (tiffOffset > (uint)(payload.Length - 4))
        {
            return null;
        }

        return payload[(4 + (int)tiffOffset)..].ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > JxlDecodingLimits.MaxMetadataBoxSize)
                {
                    throw new JxlDecodingException("A compressed metadata box expands beyond the allowed size.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            throw new JxlDecodingException("A compressed metadata box is corrupt.", ex);
        }
    }

    private static uint Fourcc(string s) =>
        ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
}
