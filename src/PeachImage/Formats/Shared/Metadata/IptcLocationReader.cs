using System.Buffers.Binary;

namespace PeachImage.Formats.Shared.Metadata;

/// <summary>Reports whether IPTC-IIM metadata names a place: City (2:90), Sub-location (2:92) or Province/State (2:95) with a non-empty value.</summary>
internal static class IptcLocationReader
{
    private const int MaxResources = 256;
    private const int MaxDataSets = 4096;
    private const ushort IptcNaaResourceId = 0x0404;

    private static ReadOnlySpan<byte> PhotoshopSignature => "Photoshop 3.0\0"u8;

    private static ReadOnlySpan<byte> ResourceSignature => "8BIM"u8;

    /// <summary>
    /// Reads a Photoshop image-resource block (a JPEG APP13 payload, with or without its <c>Photoshop 3.0\0</c> prefix, or a TIFF
    /// Photoshop tag) and looks for a place in its IPTC resource (<c>8BIM</c> 0x0404).
    /// </summary>
    public static bool ReadPhotoshop(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PhotoshopSignature))
        {
            data = data[PhotoshopSignature.Length..];
        }

        int position = 0;
        for (int resource = 0; resource < MaxResources && position + 12 <= data.Length; resource++)
        {
            if (!data.Slice(position, 4).SequenceEqual(ResourceSignature))
            {
                return false;
            }

            ushort id = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 4)..]);
            int nameLength = data[position + 6];

            // The resource name is a pascal string (length byte + text) padded to an even total length.
            int sizeOffset = position + 6 + ((1 + nameLength + 1) & ~1);

            if (sizeOffset + 4 > data.Length)
            {
                return false;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(data[sizeOffset..]);
            int dataStart = sizeOffset + 4;
            long available = data.Length - dataStart;
            int length = (int)Math.Min(size, available);
            if (id == IptcNaaResourceId)
            {
                return ReadIim(data.Slice(dataStart, length));
            }

            long next = dataStart + (long)size + (size & 1);
            if (next > data.Length)
            {
                return false;
            }

            position = (int)next;
        }

        return false;
    }

    /// <summary>Scans raw IPTC-IIM records (<c>0x1C</c>, record, dataset, 16-bit length, value) for a non-empty place.</summary>
    public static bool ReadIim(ReadOnlySpan<byte> iim)
    {
        int position = 0;
        for (int dataSet = 0; dataSet < MaxDataSets && position + 5 <= iim.Length; dataSet++)
        {
            if (iim[position] != 0x1C)
            {
                return false;
            }

            byte record = iim[position + 1];
            byte number = iim[position + 2];
            ushort size = BinaryPrimitives.ReadUInt16BigEndian(iim[(position + 3)..]);
            if ((size & 0x8000) != 0)
            {
                // Extended dataset: the length itself is stored in the following bytes. Place names are always short, so give up.
                return false;
            }

            int valueStart = position + 5;
            if (valueStart + size > iim.Length)
            {
                return false;
            }

            if (record == 2 && number is 90 or 92 or 95 && HasText(iim.Slice(valueStart, size)))
            {
                return true;
            }

            position = valueStart + size;
        }

        return false;
    }

    private static bool HasText(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
        {
            if (b is not (0 or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return true;
            }
        }

        return false;
    }
}
