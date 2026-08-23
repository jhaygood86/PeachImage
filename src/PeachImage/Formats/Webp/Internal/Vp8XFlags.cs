namespace PeachImage.Formats.Webp.Internal;

/// <summary>VP8X chunk flags-byte bit positions, shared verbatim between <c>WebpContainerReader</c> (read) and <c>WebpContainerWriter</c> (write) so the two can't drift apart.</summary>
internal static class Vp8XFlags
{
    public const byte IccBit = 0x20;
    public const byte AlphaBit = 0x10;
    public const byte ExifBit = 0x08;
    public const byte XmpBit = 0x04;
    public const byte AnimationBit = 0x02;

    /// <summary>ANMF frame-header flags byte, bit 0: clear the frame's region to the background before the next frame draws.</summary>
    public const byte AnmfDisposeToBackgroundBit = 0x01;

    /// <summary>ANMF frame-header flags byte, bit 1: overwrite the canvas region outright rather than alpha-blending onto it (inverted from <c>WebpFrameChunk.Blend</c>, which is <see langword="true"/> when this bit is clear).</summary>
    public const byte AnmfDoNotBlendBit = 0x02;
}
