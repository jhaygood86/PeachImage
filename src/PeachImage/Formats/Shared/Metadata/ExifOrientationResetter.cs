using System.Buffers.Binary;

namespace PeachImage.Formats.Shared.Metadata;

/// <summary>
/// Rewrites the orientation (tag 0x0112 of IFD0) of an EXIF payload to <see cref="ImageOrientation.Normal"/> in place, the
/// counterpart of <see cref="ExifOrientationReader"/>. Used after the pixels have been physically rotated/mirrored, so a viewer that
/// honours the tag doesn't apply the same transform a second time.
/// </summary>
internal static class ExifOrientationResetter
{
    private const ushort OrientationTag = 0x0112;
    private const ushort ShortType = 3;

    /// <summary>
    /// Sets the orientation recorded in <paramref name="exif"/>, a TIFF-structured EXIF blob optionally preceded by the
    /// <c>Exif\0\0</c> signature, to 1 (<see cref="ImageOrientation.Normal"/>). Returns <see langword="true"/> if a value other
    /// than 1 was found and overwritten; returns <see langword="false"/> and leaves <paramref name="exif"/> untouched when the tag is
    /// absent, already 1, or the data is malformed. The caller must pass a copy if the original bytes must be preserved.
    /// </summary>
    public static bool TryResetOrientation(Span<byte> exif)
    {
        if (exif.Length >= 6 && exif[0] == (byte)'E' && exif[1] == (byte)'x' && exif[2] == (byte)'i' && exif[3] == (byte)'f' && exif[4] == 0 && exif[5] == 0)
        {
            exif = exif[6..];
        }

        if (exif.Length < 8)
        {
            return false;
        }

        bool littleEndian;
        if (exif[0] == (byte)'I' && exif[1] == (byte)'I')
        {
            littleEndian = true;
        }
        else if (exif[0] == (byte)'M' && exif[1] == (byte)'M')
        {
            littleEndian = false;
        }
        else
        {
            return false;
        }

        if (ReadU16(exif, 2, littleEndian) != 42)
        {
            return false;
        }

        uint ifdOffset = ReadU32(exif, 4, littleEndian);
        if (ifdOffset > exif.Length - 2)
        {
            return false;
        }

        int entries = ReadU16(exif, (int)ifdOffset, littleEndian);
        long entryStart = ifdOffset + 2L;
        for (int i = 0; i < entries; i++)
        {
            long entry = entryStart + (i * 12L);
            if (entry + 12 > exif.Length)
            {
                return false;
            }

            if (ReadU16(exif, (int)entry, littleEndian) != OrientationTag)
            {
                continue;
            }

            if (ReadU16(exif, (int)entry + 2, littleEndian) != ShortType || ReadU32(exif, (int)entry + 4, littleEndian) < 1)
            {
                return false;
            }

            // A single SHORT value is stored inline in the first two bytes of the value field.
            int valueOffset = (int)entry + 8;
            if (ReadU16(exif, valueOffset, littleEndian) == (ushort)ImageOrientation.Normal)
            {
                return false;
            }

            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(exif[valueOffset..], (ushort)ImageOrientation.Normal);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(exif[valueOffset..], (ushort)ImageOrientation.Normal);
            }

            return true;
        }

        return false;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
