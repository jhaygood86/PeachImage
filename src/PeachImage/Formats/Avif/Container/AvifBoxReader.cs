using System.Buffers.Binary;
using PeachImage.Formats.Avif.Internal;

namespace PeachImage.Formats.Avif.Container;

/// <summary>One ISOBMFF box's four-character type and payload location within the buffered file.</summary>
internal readonly record struct AvifBox(string FourCc, int PayloadOffset, int PayloadLength);

/// <summary>
/// Generic recursive ISOBMFF box-header reader. AVIF's container is a box <em>tree</em> with item data
/// addressed by absolute offset (<c>iloc</c>), unlike WebP's flat, sequentially-read RIFF chunk stream —
/// so the whole file is buffered up front (see <c>AvifContainerReader</c>) and this reader walks one
/// level of the tree at a time; callers recurse into container boxes (<c>meta</c>, <c>iprp</c>,
/// <c>ipco</c>) explicitly rather than this type assuming any fixed shape, mirroring
/// <c>WebpContainerReader</c>'s dispatch-by-FourCC, don't-assume-order posture.
/// </summary>
internal static class AvifBoxReader
{
    /// <summary>Reads every top-level box in <c>data[start..end)</c>. Every box is validated to not claim to extend past <paramref name="end"/> — the DoS guard for this layer, in the spirit of <c>Vp8BoolDecoder</c>'s constructor bounds check.</summary>
    public static List<AvifBox> ReadBoxes(byte[] data, int start, int end, int depth)
    {
        if (depth > AvifDecodingLimits.MaxBoxNestingDepth)
        {
            throw new AvifDecodingException("AVIF container is nested more deeply than the supported limit.");
        }

        var boxes = new List<AvifBox>();
        int offset = start;

        while (offset < end)
        {
            if (end - offset < 8)
            {
                throw new AvifDecodingException("Truncated ISOBMFF box header.");
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            string fourCc = AvifBinaryReader.ReadFourCc(data, offset + 4);
            int headerLength = 8;

            if (size == 1)
            {
                if (end - offset < 16)
                {
                    throw new AvifDecodingException("Truncated ISOBMFF box header (64-bit largesize).");
                }

                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8)));
                headerLength = 16;
            }
            else if (size == 0)
            {
                // size == 0 means "extends to the end of the containing region" (typically only legal for
                // the last box in a file/container) — never seen in practice for AVIF's own boxes, but
                // handled per spec rather than rejected outright.
                size = end - offset;
            }

            if (size < headerLength || size > end - offset)
            {
                throw new AvifDecodingException($"Box '{fourCc}' has an invalid size or extends past its container.");
            }

            int payloadOffset = checked(offset + headerLength);
            int payloadLength = checked((int)size - headerLength);

            boxes.Add(new AvifBox(fourCc, payloadOffset, payloadLength));
            offset = checked(offset + (int)size);
        }

        return boxes;
    }
}
