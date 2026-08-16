namespace PeachImage.Formats.Avif.Container;

/// <summary>The <c>pixi</c> property: per-channel bit depth. Used as a cross-check against <c>av1C</c>'s <c>high_bitdepth</c> flag; not yet wired into decode (Phase 1 doesn't decode pixels), but parsed now so later phases have it available.</summary>
internal readonly record struct AvifPixi(IReadOnlyList<int> BitsPerChannel);

internal static class AvifPixiBox
{
    public static AvifPixi Parse(byte[] data, AvifBox box)
    {
        if (box.PayloadLength < 5)
        {
            throw new AvifDecodingException("Truncated 'pixi' box.");
        }

        int offset = box.PayloadOffset + 4; // version(1) + flags(3)
        int numChannels = AvifBinaryReader.ReadUInt8(data, ref offset);

        if (box.PayloadOffset + box.PayloadLength < offset + numChannels)
        {
            throw new AvifDecodingException("Truncated 'pixi' box.");
        }

        var bits = new List<int>(numChannels);
        for (int i = 0; i < numChannels; i++)
        {
            bits.Add(AvifBinaryReader.ReadUInt8(data, ref offset));
        }

        return new AvifPixi(bits);
    }
}
