namespace PeachImage.Formats.Webp.Decoding;

/// <summary>The seam between container parsing and the VP8 (lossy) bitstream decoder — the container layer never needs to know about YUV subsampling, DCT, prediction, or loop filtering.</summary>
internal interface IWebpLossyBitstreamDecoder
{
    /// <summary>
    /// Returns already-converted pixel data plus the bitstream's own width/height (independent of any VP8X
    /// canvas size).
    /// </summary>
    /// <param name="vp8Bytes">Exactly a <c>VP8 </c> chunk's payload (no RIFF/chunk framing).</param>
    /// <param name="alphaPlane">
    /// The already-decoded <c>ALPH</c> chunk plane (tightly packed, one byte per pixel, <c>width*height</c>
    /// long), or <see langword="null"/> for an opaque image. Passed in — rather than decoded internally — so
    /// the caller can decode it once, at the bitstream's own dimensions, and the frame decoder can splice it
    /// into the pixel-conversion pass it already runs instead of the caller building a second, whole-image
    /// RGB24-&gt;RGBA32 copy afterward.
    /// </param>
    Vp8DecodedFrame Decode(ReadOnlyMemory<byte> vp8Bytes, byte[]? alphaPlane);
}

/// <summary>A fully decoded VP8 (lossy) frame: tightly-packed pixels plus the bitstream's own dimensions.</summary>
internal readonly struct Vp8DecodedFrame
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary><see cref="PixelFormat.Rgb24"/> when no alpha plane was supplied, <see cref="PixelFormat.Rgba32"/> otherwise.</summary>
    public required PixelFormat PixelFormat { get; init; }

    /// <summary>Tightly packed (no row padding), matching <see cref="Image"/>'s row convention for <see cref="PixelFormat"/>.</summary>
    public required byte[] Pixels { get; init; }
}
