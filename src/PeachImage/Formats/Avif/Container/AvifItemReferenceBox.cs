namespace PeachImage.Formats.Avif.Container;

/// <summary>One typed reference list from <c>iref</c>: e.g. a <c>dimg</c> reference from a <c>grid</c> item to its ordered tile items, or an <c>auxl</c> reference from a color item to its alpha auxiliary item.</summary>
internal readonly record struct AvifItemReference(uint FromItemId, string ReferenceType, IReadOnlyList<uint> ToItemIds);

/// <summary>Parses the <c>iref</c> box: a sequence of typed single-item-type reference lists (ISO/IEC 14496-12 §8.11.12).</summary>
internal static class AvifItemReferenceBox
{
    public static List<AvifItemReference> Parse(byte[] data, AvifBox box)
    {
        int offset = box.PayloadOffset;
        int end = box.PayloadOffset + box.PayloadLength;

        byte version = data[offset];
        offset += 4;

        var result = new List<AvifItemReference>();

        while (offset < end)
        {
            if (offset + 8 > end)
            {
                throw new AvifDecodingException("Truncated 'iref' entry.");
            }

            int entryStart = offset;
            uint size = AvifBinaryReader.ReadUInt32(data, ref offset);
            string referenceType = AvifBinaryReader.ReadFourCc(data, offset);
            offset += 4;

            if (size < 8)
            {
                throw new AvifDecodingException("Malformed 'iref' entry size.");
            }

            int entryEnd = checked(entryStart + (int)size);
            if (entryEnd > end)
            {
                throw new AvifDecodingException("'iref' entry extends past its containing box.");
            }

            uint fromItemId = version == 0
                ? AvifBinaryReader.ReadUInt16(data, ref offset)
                : AvifBinaryReader.ReadUInt32(data, ref offset);
            int referenceCount = AvifBinaryReader.ReadUInt16(data, ref offset);

            var toIds = new List<uint>(referenceCount);
            for (int i = 0; i < referenceCount; i++)
            {
                toIds.Add(version == 0
                    ? AvifBinaryReader.ReadUInt16(data, ref offset)
                    : AvifBinaryReader.ReadUInt32(data, ref offset));
            }

            result.Add(new AvifItemReference(fromItemId, referenceType, toIds));
            offset = entryEnd;
        }

        return result;
    }
}
