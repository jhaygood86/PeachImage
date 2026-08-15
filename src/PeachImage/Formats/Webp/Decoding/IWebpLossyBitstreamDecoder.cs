namespace PeachImage.Formats.Webp.Decoding;

/// <summary>The seam between container parsing and the VP8 (lossy) bitstream decoder — the container layer never needs to know about YUV subsampling, DCT, prediction, or loop filtering.</summary>
internal interface IWebpLossyBitstreamDecoder
{
    /// <summary><paramref name="vp8Bytes"/> is exactly a <c>VP8 </c> chunk's payload (no RIFF/chunk framing). Returns already-converted RGB pixel data plus the bitstream's own width/height (independent of any VP8X canvas size).</summary>
    Vp8DecodedFrame Decode(ReadOnlyMemory<byte> vp8Bytes);
}

/// <summary>A fully decoded VP8 (lossy) frame: tightly-packed RGB24 pixels plus the bitstream's own dimensions.</summary>
internal readonly struct Vp8DecodedFrame
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Tightly packed (no row padding), matching <see cref="Image"/>'s row convention for <see cref="PixelFormat.Rgb24"/>.</summary>
    public required byte[] Rgb24Pixels { get; init; }
}
