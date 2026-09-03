namespace PeachImage.Formats.Avif.Container;

/// <summary>The <c>auxC</c> property: an auxiliary image item's type URN, used to confirm an <c>iref auxl</c>-referenced item is really alpha rather than some other auxiliary (e.g. a depth map).</summary>
internal static class AvifAuxCBox
{
    private const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";

    /// <summary>Seen from some older/non-libavif encoders in place of <see cref="AlphaUrn"/>.</summary>
    private const string AlphaUrnLegacy = "urn:mpeg:hevc:2015:auxid:1";

    public static bool IsAlpha(byte[] data, AvifBox box)
    {
        if (box.PayloadLength < 4)
        {
            throw new AvifDecodingException("Truncated 'auxC' box.");
        }

        int offset = box.PayloadOffset + 4; // version(1) + flags(3)
        int end = box.PayloadOffset + box.PayloadLength;
        string auxType = AvifBinaryReader.ReadCString(data, ref offset, end);
        return auxType is AlphaUrn or AlphaUrnLegacy;
    }

    /// <summary>Writes an <c>auxC</c> box tagging an item as the alpha auxiliary image, using <see cref="AlphaUrn"/> -- the write-side inverse of <see cref="IsAlpha"/>.</summary>
    public static byte[] Build()
    {
        byte[] urn = System.Text.Encoding.ASCII.GetBytes(AlphaUrn);
        var payload = new byte[urn.Length + 1]; // aux_type cstring: URN bytes + null terminator (already 0)
        Array.Copy(urn, payload, urn.Length);
        return AvifBoxWriter.FullBox("auxC", version: 0, flags: 0, payload);
    }
}
