using System.Buffers.Binary;

namespace PeachImage.Formats.Avif.Container;

/// <summary>
/// A <c>grid</c> derived-image item's own small payload (ISO/IEC 23008-12 §6.6.2.3.2 "ImageGrid"): how
/// many rows/columns of tiles compose the image, and the grid's declared output size (tiles may overhang
/// this on the last row/column — cropped during compositing, not here).
/// </summary>
internal readonly record struct AvifGridDescriptor(int Rows, int Columns, int OutputWidth, int OutputHeight)
{
    public static AvifGridDescriptor Parse(ReadOnlySpan<byte> itemData)
    {
        if (itemData.Length < 8)
        {
            throw new AvifDecodingException("Truncated 'grid' item descriptor.");
        }

        byte flags = itemData[1];
        int rows = itemData[2] + 1;
        int columns = itemData[3] + 1;
        bool largeFields = (flags & 0x1) != 0;

        int offset = 4;
        int width, height;

        if (largeFields)
        {
            if (itemData.Length < offset + 8)
            {
                throw new AvifDecodingException("Truncated 'grid' item descriptor.");
            }

            width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(itemData[offset..]));
            height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(itemData[(offset + 4)..]));
        }
        else
        {
            if (itemData.Length < offset + 4)
            {
                throw new AvifDecodingException("Truncated 'grid' item descriptor.");
            }

            width = BinaryPrimitives.ReadUInt16BigEndian(itemData[offset..]);
            height = BinaryPrimitives.ReadUInt16BigEndian(itemData[(offset + 2)..]);
        }

        return new AvifGridDescriptor(rows, columns, width, height);
    }
}
