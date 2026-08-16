namespace PeachImage.Formats.Avif.Container;

/// <summary>The <c>ispe</c> property: an item's declared spatial extents (width/height).</summary>
internal readonly record struct AvifIspe(int Width, int Height);

internal static class AvifIspeBox
{
    public static AvifIspe Parse(byte[] data, AvifBox box)
    {
        if (box.PayloadLength < 12)
        {
            throw new AvifDecodingException("Truncated 'ispe' box.");
        }

        int offset = box.PayloadOffset + 4; // version(1) + flags(3)
        int width = checked((int)AvifBinaryReader.ReadUInt32(data, ref offset));
        int height = checked((int)AvifBinaryReader.ReadUInt32(data, ref offset));
        return new AvifIspe(width, height);
    }
}
