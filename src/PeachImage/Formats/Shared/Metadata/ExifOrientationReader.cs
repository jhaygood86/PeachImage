using System.Buffers.Binary;

namespace PeachImage.Formats.Shared.Metadata;

/// <summary>Reads the orientation (tag 0x0112 of IFD0) out of an EXIF payload without parsing anything else.</summary>
internal static class ExifOrientationReader
{
    private const ushort OrientationTag = 0x0112;
    private const ushort ShortType = 3;

    /// <summary>
    /// Returns the orientation recorded in <paramref name="exif"/>, a TIFF-structured EXIF blob optionally preceded by the
    /// <c>Exif\0\0</c> signature, or <see cref="ImageOrientation.Normal"/> when it is absent, out of range or malformed.
    /// </summary>
    public static ImageOrientation Read(ReadOnlySpan<byte> exif)
    {
        if (exif.Length >= 6 && exif[0] == (byte)'E' && exif[1] == (byte)'x' && exif[2] == (byte)'i' && exif[3] == (byte)'f' && exif[4] == 0 && exif[5] == 0)
        {
            exif = exif[6..];
        }

        return ReadTiff(exif);
    }

    /// <summary>As <see cref="Read"/>, for a payload that starts at the TIFF header (<c>II*\0</c> or <c>MM\0*</c>).</summary>
    public static ImageOrientation ReadTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return ImageOrientation.Normal;
        }

        bool littleEndian;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I')
        {
            littleEndian = true;
        }
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M')
        {
            littleEndian = false;
        }
        else
        {
            return ImageOrientation.Normal;
        }

        if (ReadU16(tiff, 2, littleEndian) != 42)
        {
            return ImageOrientation.Normal;
        }

        uint ifdOffset = ReadU32(tiff, 4, littleEndian);
        if (ifdOffset > tiff.Length - 2)
        {
            return ImageOrientation.Normal;
        }

        int entries = ReadU16(tiff, (int)ifdOffset, littleEndian);
        long entryStart = ifdOffset + 2L;
        for (int i = 0; i < entries; i++)
        {
            long entry = entryStart + (i * 12L);
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            if (ReadU16(tiff, (int)entry, littleEndian) != OrientationTag)
            {
                continue;
            }

            if (ReadU16(tiff, (int)entry + 2, littleEndian) != ShortType || ReadU32(tiff, (int)entry + 4, littleEndian) < 1)
            {
                return ImageOrientation.Normal;
            }

            // A single SHORT value is stored inline in the first two bytes of the value field.
            return FromValue(ReadU16(tiff, (int)entry + 8, littleEndian));
        }

        return ImageOrientation.Normal;
    }

    /// <summary>Maps an EXIF orientation value to <see cref="ImageOrientation"/>; anything outside 1 to 8 is <see cref="ImageOrientation.Normal"/>.</summary>
    public static ImageOrientation FromValue(int value) =>
        value is >= 1 and <= 8 ? (ImageOrientation)value : ImageOrientation.Normal;

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
