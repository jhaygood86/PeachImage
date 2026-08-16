using PeachImage.Formats.Avif.Internal;

namespace PeachImage.Formats.Avif.Container;

/// <summary>A single extent (byte range) of an item's data, as an absolute offset within the buffered file (or, for <c>construction_method == 1</c>, within the <c>idat</c> box's payload — resolved to absolute by <c>AvifItemAssembler</c>).</summary>
internal readonly record struct AvifItemExtent(long Offset, long Length);

/// <summary>One item's resolved <c>iloc</c> entry: how its data is constructed and where its bytes live.</summary>
internal sealed class AvifItemLocation
{
    public required uint ItemId { get; init; }

    /// <summary><c>0</c> = file offset, <c>1</c> = <c>idat</c>-box offset. <c>construction_method == 2</c> ("item offset", referencing another item's data) is rejected during parsing — vanishingly rare and not needed for still-image AVIF.</summary>
    public required int ConstructionMethod { get; init; }

    public required IReadOnlyList<AvifItemExtent> Extents { get; init; }
}

/// <summary>Parses the <c>iloc</c> box (ISO/IEC 14496-12 §8.11.3): item id -> byte extents.</summary>
internal static class AvifItemLocationBox
{
    public static Dictionary<uint, AvifItemLocation> Parse(byte[] data, AvifBox box)
    {
        int offset = box.PayloadOffset;
        int end = box.PayloadOffset + box.PayloadLength;

        byte version = data[offset];
        offset += 4; // version(1) + flags(3)

        byte sizesByte1 = AvifBinaryReader.ReadUInt8(data, ref offset);
        int offsetSize = sizesByte1 >> 4;
        int lengthSize = sizesByte1 & 0xF;

        byte sizesByte2 = AvifBinaryReader.ReadUInt8(data, ref offset);
        int baseOffsetSize = sizesByte2 >> 4;
        int indexSize = sizesByte2 & 0xF; // only meaningful for version 1/2; reserved (and ignored) otherwise

        int itemCount = version < 2
            ? AvifBinaryReader.ReadUInt16(data, ref offset)
            : checked((int)AvifBinaryReader.ReadUInt32(data, ref offset));

        if (itemCount > AvifDecodingLimits.MaxItemCount)
        {
            throw new AvifDecodingException($"'iloc' box declares {itemCount} items, exceeding the supported maximum of {AvifDecodingLimits.MaxItemCount}.");
        }

        var result = new Dictionary<uint, AvifItemLocation>();

        for (int i = 0; i < itemCount; i++)
        {
            uint itemId = version < 2
                ? AvifBinaryReader.ReadUInt16(data, ref offset)
                : AvifBinaryReader.ReadUInt32(data, ref offset);

            int constructionMethod = 0;
            if (version is 1 or 2)
            {
                int reservedAndMethod = AvifBinaryReader.ReadUInt16(data, ref offset);
                constructionMethod = reservedAndMethod & 0xF;
            }

            AvifBinaryReader.ReadUInt16(data, ref offset); // data_reference_index -- always "this file" for our scope

            long baseOffset = checked((long)AvifBinaryReader.ReadUIntN(data, ref offset, baseOffsetSize));

            int extentCount = AvifBinaryReader.ReadUInt16(data, ref offset);
            var extents = new List<AvifItemExtent>(extentCount);
            for (int j = 0; j < extentCount; j++)
            {
                if (version is 1 or 2 && indexSize > 0)
                {
                    AvifBinaryReader.ReadUIntN(data, ref offset, indexSize); // extent_index -- unused for our scope
                }

                long extentOffset = checked((long)AvifBinaryReader.ReadUIntN(data, ref offset, offsetSize));
                long extentLength = checked((long)AvifBinaryReader.ReadUIntN(data, ref offset, lengthSize));
                extents.Add(new AvifItemExtent(baseOffset + extentOffset, extentLength));
            }

            if (constructionMethod == 2)
            {
                throw new AvifDecodingException($"Item {itemId} uses iloc construction_method=2 (item offset), which is not supported.");
            }

            result[itemId] = new AvifItemLocation
            {
                ItemId = itemId,
                ConstructionMethod = constructionMethod,
                Extents = extents,
            };
        }

        if (offset > end)
        {
            throw new AvifDecodingException("Truncated 'iloc' box.");
        }

        return result;
    }
}
