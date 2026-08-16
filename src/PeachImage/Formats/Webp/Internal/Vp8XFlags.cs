namespace PeachImage.Formats.Webp.Internal;

/// <summary>VP8X chunk flags-byte bit positions, shared verbatim between <c>WebpContainerReader</c> (read) and <c>WebpContainerWriter</c> (write) so the two can't drift apart.</summary>
internal static class Vp8XFlags
{
    public const byte IccBit = 0x20;
    public const byte AlphaBit = 0x10;
    public const byte ExifBit = 0x08;
    public const byte XmpBit = 0x04;
    public const byte AnimationBit = 0x02;
}
