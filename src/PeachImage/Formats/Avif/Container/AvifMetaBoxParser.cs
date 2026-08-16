namespace PeachImage.Formats.Avif.Container;

/// <summary>Everything <see cref="AvifItemAssembler"/> needs, gathered generically from <c>meta</c>'s children.</summary>
internal sealed class AvifMetaBoxInfo
{
    public required uint? PrimaryItemId { get; init; }

    public required IReadOnlyDictionary<uint, AvifItemInfo> Items { get; init; }

    public required IReadOnlyDictionary<uint, AvifItemLocation> Locations { get; init; }

    public required IReadOnlyList<AvifItemReference> References { get; init; }

    public required AvifItemPropertiesBox Properties { get; init; }

    /// <summary>The <c>idat</c> box's payload offset in the buffered file, if present -- <c>construction_method == 1</c> item extents are relative to this.</summary>
    public int? IdatPayloadOffset { get; init; }
}

/// <summary>
/// Walks <c>meta</c>'s children by FourCC (mirrors <c>WebpContainerReader</c>'s dispatch-by-FourCC,
/// don't-assume-order posture), delegating each child to its own box parser. Purely structural -- does
/// not resolve items into decodable byte streams or interpret grid/alpha semantics; that's
/// <see cref="AvifItemAssembler"/>'s job.
/// </summary>
internal static class AvifMetaBoxParser
{
    public static AvifMetaBoxInfo Parse(byte[] data, AvifBox metaBox, int depth)
    {
        // 'meta' is itself a FullBox (4-byte version+flags header) before its children begin -- unlike
        // 'iprp'/'ipco', which are plain container Boxes.
        int childrenStart = metaBox.PayloadOffset + 4;
        int childrenEnd = metaBox.PayloadOffset + metaBox.PayloadLength;
        var children = AvifBoxReader.ReadBoxes(data, childrenStart, childrenEnd, depth);

        uint? primaryItemId = null;
        IReadOnlyDictionary<uint, AvifItemInfo> items = new Dictionary<uint, AvifItemInfo>();
        IReadOnlyDictionary<uint, AvifItemLocation> locations = new Dictionary<uint, AvifItemLocation>();
        IReadOnlyList<AvifItemReference> references = [];
        AvifItemPropertiesBox properties = AvifItemPropertiesBox.Empty;
        int? idatOffset = null;

        foreach (var child in children)
        {
            switch (child.FourCc)
            {
                case "pitm":
                    primaryItemId = ParsePitm(data, child);
                    break;

                case "iloc":
                    locations = AvifItemLocationBox.Parse(data, child);
                    break;

                case "iinf":
                    items = AvifItemInfoBox.Parse(data, child);
                    break;

                case "iref":
                    references = AvifItemReferenceBox.Parse(data, child);
                    break;

                case "iprp":
                    properties = AvifItemPropertiesBox.Parse(data, child, depth + 1);
                    break;

                case "idat":
                    idatOffset = child.PayloadOffset;
                    break;

                default:
                    // 'hdlr', 'dinf', thumbnail-related boxes, etc. -- not needed for still-image decode.
                    break;
            }
        }

        return new AvifMetaBoxInfo
        {
            PrimaryItemId = primaryItemId,
            Items = items,
            Locations = locations,
            References = references,
            Properties = properties,
            IdatPayloadOffset = idatOffset,
        };
    }

    private static uint ParsePitm(byte[] data, AvifBox box)
    {
        int offset = box.PayloadOffset;
        byte version = data[offset];
        offset += 4;
        return version == 0 ? AvifBinaryReader.ReadUInt16(data, ref offset) : AvifBinaryReader.ReadUInt32(data, ref offset);
    }
}
