using System.Buffers.Binary;

namespace PeachImage.Formats.Shared.Metadata;

/// <summary>
/// Looks in a TIFF-structured EXIF block (or a whole TIFF file) for GPS position tags, without trusting anything it declares:
/// every offset and count is bounds-checked against the data actually present, the IFD chain and the entry count are capped,
/// a revisited IFD ends the walk, and nothing is allocated.
/// </summary>
internal static class ExifLocationReader
{
    /// <summary>The most IFDs followed along the chain of next-IFD offsets.</summary>
    public const int MaxIfdChain = 8;

    /// <summary>The most entries examined in any one IFD.</summary>
    public const int MaxEntries = 4096;

    private const ushort GpsIfdPointerTag = 0x8825;
    private const ushort XmpTag = 700;
    private const ushort IptcTag = 33723;
    private const ushort PhotoshopTag = 34377;

    private const ushort GpsLatitudeRef = 0x0001;
    private const ushort GpsLatitude = 0x0002;
    private const ushort GpsLongitudeRef = 0x0003;
    private const ushort GpsLongitude = 0x0004;
    private const ushort GpsDestLatitudeRef = 0x0013;
    private const ushort GpsDestLatitude = 0x0014;
    private const ushort GpsDestLongitudeRef = 0x0015;
    private const ushort GpsDestLongitude = 0x0016;

    private const ushort AsciiType = 2;
    private const ushort RationalType = 5;

    /// <summary>
    /// Reads <paramref name="data"/>, an EXIF block (optionally preceded by <c>Exif\0\0</c>) or, when
    /// <paramref name="isTiffFile"/> is true, a whole TIFF file, whose XMP, IPTC and Photoshop tags are then examined as well.
    /// Malformed or truncated input yields whatever was found before the problem, never an exception.
    /// </summary>
    public static LocationInfo Read(ReadOnlySpan<byte> data, bool isTiffFile = false)
    {
        if (!isTiffFile && data.Length >= 6 && data[0] == (byte)'E' && data[1] == (byte)'x' && data[2] == (byte)'i' && data[3] == (byte)'f' && data[4] == 0 && data[5] == 0)
        {
            data = data[6..];
        }

        if (data.Length < 8)
        {
            return default;
        }

        bool littleEndian;
        if (data[0] == (byte)'I' && data[1] == (byte)'I')
        {
            littleEndian = true;
        }
        else if (data[0] == (byte)'M' && data[1] == (byte)'M')
        {
            littleEndian = false;
        }
        else
        {
            return default;
        }

        if (ReadU16(data, 0 + 2, littleEndian) != 42)
        {
            return default;
        }

        bool xmp = false;
        bool iptc = false;
        long gpsOffset = -1;
        long ifd = ReadU32(data, 4, littleEndian);
        Span<long> seen = stackalloc long[MaxIfdChain];

        for (int chain = 0; chain < MaxIfdChain; chain++)
        {
            if (ifd == 0 || ifd > data.Length - 2 || seen[..chain].Contains(ifd))
            {
                break;
            }

            seen[chain] = ifd;
            int declared = ReadU16(data, (int)ifd, littleEndian);
            long entriesStart = ifd + 2;
            int examined = Math.Min(declared, MaxEntries);
            for (int i = 0; i < examined; i++)
            {
                long entry = entriesStart + (i * 12L);
                if (entry + 12 > data.Length)
                {
                    break;
                }

                ushort tag = ReadU16(data, (int)entry, littleEndian);
                if (tag == GpsIfdPointerTag)
                {
                    if (gpsOffset < 0)
                    {
                        gpsOffset = ReadU32(data, (int)entry + 8, littleEndian);
                    }
                }
                else if (isTiffFile && tag is XmpTag or IptcTag or PhotoshopTag)
                {
                    var payload = EntryPayload(data, (int)entry, littleEndian);
                    xmp |= tag == XmpTag && XmpLocationScanner.NamesLocation(payload);
                    iptc |= tag == IptcTag && IptcLocationReader.ReadIim(payload);
                    iptc |= tag == PhotoshopTag && IptcLocationReader.ReadPhotoshop(payload);
                }
            }

            long nextPointer = entriesStart + (declared * 12L);
            if (nextPointer + 4 > data.Length)
            {
                break;
            }

            ifd = ReadU32(data, (int)nextPointer, littleEndian);
        }

        bool gpsPresent = false;
        ExifLocation? location = null;
        if (gpsOffset >= 0)
        {
            gpsPresent = ReadGps(data, gpsOffset, littleEndian, out var read);
            location = gpsPresent ? read : null;
        }

        return new LocationInfo(gpsPresent, location, xmp, iptc);
    }

    private static bool ReadGps(ReadOnlySpan<byte> data, long offset, bool littleEndian, out ExifLocation location)
    {
        location = default;
        if (offset > data.Length - 2)
        {
            return false;
        }

        int declared = ReadU16(data, (int)offset, littleEndian);
        int examined = Math.Min(declared, MaxEntries);
        bool present = false;
        double? lat = null, lon = null, destLat = null, destLon = null;
        char? latRef = null, lonRef = null, destLatRef = null, destLonRef = null;

        for (int i = 0; i < examined; i++)
        {
            long entry = offset + 2 + (i * 12L);
            if (entry + 12 > data.Length)
            {
                break;
            }

            switch (ReadU16(data, (int)entry, littleEndian))
            {
                case GpsLatitudeRef: latRef = ReadRef(data, (int)entry, littleEndian); break;
                case GpsLongitudeRef: lonRef = ReadRef(data, (int)entry, littleEndian); break;
                case GpsDestLatitudeRef: destLatRef = ReadRef(data, (int)entry, littleEndian); break;
                case GpsDestLongitudeRef: destLonRef = ReadRef(data, (int)entry, littleEndian); break;
                case GpsLatitude: present = true; lat = ReadDegrees(data, (int)entry, littleEndian); break;
                case GpsLongitude: present = true; lon = ReadDegrees(data, (int)entry, littleEndian); break;
                case GpsDestLatitude: present = true; destLat = ReadDegrees(data, (int)entry, littleEndian); break;
                case GpsDestLongitude: present = true; destLon = ReadDegrees(data, (int)entry, littleEndian); break;
            }
        }

        location = new ExifLocation(lat, latRef, lon, lonRef, destLat, destLatRef, destLon, destLonRef);
        return present;
    }

    // Degrees, minutes and seconds as up to three rationals; anything that doesn't fit the data, or divides by zero, is null.
    private static double? ReadDegrees(ReadOnlySpan<byte> data, int entry, bool littleEndian)
    {
        if (ReadU16(data, entry + 2, littleEndian) != RationalType)
        {
            return null;
        }

        uint count = ReadU32(data, entry + 4, littleEndian);
        if (count is 0 or > 3)
        {
            return null;
        }

        long offset = ReadU32(data, entry + 8, littleEndian);
        if (offset + (count * 8L) > data.Length)
        {
            return null;
        }

        double total = 0;
        double divisor = 1;
        for (int i = 0; i < count; i++)
        {
            uint numerator = ReadU32(data, (int)offset + (i * 8), littleEndian);
            uint denominator = ReadU32(data, (int)offset + (i * 8) + 4, littleEndian);
            if (denominator == 0)
            {
                return null;
            }

            total += numerator / (double)denominator / divisor;
            divisor *= 60;
        }

        return double.IsFinite(total) ? total : null;
    }

    private static char? ReadRef(ReadOnlySpan<byte> data, int entry, bool littleEndian)
    {
        if (ReadU16(data, entry + 2, littleEndian) != AsciiType)
        {
            return null;
        }

        uint count = ReadU32(data, entry + 4, littleEndian);
        if (count == 0)
        {
            return null;
        }

        long position = count <= 4 ? entry + 8 : ReadU32(data, entry + 8, littleEndian);
        if (position >= data.Length || data[(int)position] == 0)
        {
            return null;
        }

        return (char)data[(int)position];
    }

    // The bytes an entry's value occupies: inline in its 4-byte value field when they fit, else at the offset it holds, clipped to the data.
    private static ReadOnlySpan<byte> EntryPayload(ReadOnlySpan<byte> data, int entry, bool littleEndian)
    {
        int typeSize = ReadU16(data, entry + 2, littleEndian) switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        long byteCount = typeSize * (long)ReadU32(data, entry + 4, littleEndian);
        if (byteCount == 0)
        {
            return default;
        }

        long start = byteCount <= 4 ? entry + 8 : ReadU32(data, entry + 8, littleEndian);
        if (start >= data.Length)
        {
            return default;
        }

        return data.Slice((int)start, (int)Math.Min(byteCount, data.Length - start));
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
