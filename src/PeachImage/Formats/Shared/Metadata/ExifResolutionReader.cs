using System.Buffers.Binary;

namespace PeachImage.Formats.Shared.Metadata;

/// <summary>Reads <c>XResolution</c>, <c>YResolution</c> and <c>ResolutionUnit</c> out of IFD0 of an EXIF payload.</summary>
internal static class ExifResolutionReader
{
    private const ushort XResolutionTag = 0x011A;
    private const ushort YResolutionTag = 0x011B;
    private const ushort ResolutionUnitTag = 0x0128;
    private const ushort ShortType = 3;
    private const ushort RationalType = 5;

    /// <summary>
    /// The horizontal and vertical density of <paramref name="exif"/> (a TIFF-structured blob, optionally preceded by <c>Exif\0\0</c>)
    /// in pixels per metre, or <see langword="null"/> when it records none, records only an aspect ratio, or is malformed.
    /// </summary>
    public static (double X, double Y)? Read(ReadOnlySpan<byte> exif)
    {
        if (exif.Length >= 6 && exif[..6].SequenceEqual("Exif\0\0"u8))
        {
            exif = exif[6..];
        }

        if (exif.Length < 8)
        {
            return null;
        }

        bool little;
        if (exif[0] == (byte)'I' && exif[1] == (byte)'I')
        {
            little = true;
        }
        else if (exif[0] == (byte)'M' && exif[1] == (byte)'M')
        {
            little = false;
        }
        else
        {
            return null;
        }

        if (U16(exif, 2, little) != 42)
        {
            return null;
        }

        uint ifd = U32(exif, 4, little);
        if (ifd > exif.Length - 2)
        {
            return null;
        }

        int count = U16(exif, (int)ifd, little);
        double? x = null, y = null;
        int unit = 2;
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2L + (i * 12L);
            if (entry + 12 > exif.Length)
            {
                break;
            }

            ushort tag = U16(exif, (int)entry, little);
            ushort type = U16(exif, (int)entry + 2, little);
            switch (tag)
            {
                case XResolutionTag:
                    x = Rational(exif, (int)entry, type, little);
                    break;
                case YResolutionTag:
                    y = Rational(exif, (int)entry, type, little);
                    break;
                case ResolutionUnitTag when type == ShortType:
                    unit = U16(exif, (int)entry + 8, little);
                    break;
            }
        }

        if (x is not > 0 || y is not > 0 || ResolutionUnits.FromTiffUnit(unit) is not { } factor)
        {
            return null;
        }

        return (x.Value * factor, y.Value * factor);
    }

    private static double? Rational(ReadOnlySpan<byte> data, int entry, ushort type, bool little)
    {
        if (type != RationalType)
        {
            return null;
        }

        uint offset = U32(data, entry + 8, little);
        if (offset > data.Length - 8)
        {
            return null;
        }

        uint denominator = U32(data, (int)offset + 4, little);
        return denominator == 0 ? null : (double)U32(data, (int)offset, little) / denominator;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint U32(ReadOnlySpan<byte> data, int offset, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
