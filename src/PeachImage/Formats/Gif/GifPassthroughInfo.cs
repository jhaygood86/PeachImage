namespace PeachImage.Formats.Gif;

/// <summary>
/// The raw chunk data needed to embed a GIF's first frame directly into another container (e.g. a PDF
/// <c>/LZWDecode</c> image stream) without decompressing/recompressing its pixel data. See
/// <see cref="GifPassthrough.TryRead"/>.
/// </summary>
/// <param name="Width">The first frame's Image Descriptor width, in pixels — what <see cref="LzwData"/> decompresses to (<see cref="Width"/> × <see cref="Height"/> palette-index bytes).</param>
/// <param name="Height">The first frame's Image Descriptor height, in pixels.</param>
/// <param name="Left">The first frame's Image Descriptor horizontal offset onto the logical screen. Usually 0, but not guaranteed by the format.</param>
/// <param name="Top">The first frame's Image Descriptor vertical offset onto the logical screen. Usually 0, but not guaranteed by the format.</param>
/// <param name="CanvasWidth">The GIF's logical screen width, from the header. Compare against <see cref="Width"/>/<see cref="Left"/> to detect whether the frame covers the full canvas.</param>
/// <param name="CanvasHeight">The GIF's logical screen height, from the header.</param>
/// <param name="MinCodeSize">
/// The frame's LZW minimum code size byte. GIF's LZW starts codes at <c>MinCodeSize + 1</c> bits with a
/// Clear code of <c>1 &lt;&lt; MinCodeSize</c>, while PDF's <c>/LZWDecode</c> filter is fixed at a 9-bit
/// start and Clear code 256 — the two are only byte-for-byte compatible when <c>MinCodeSize == 8</c>.
/// Many real-world small-palette GIFs use a smaller value; passing <see cref="LzwData"/> through to
/// <c>/LZWDecode</c> for those would silently misinterpret the bitstream from the first code onward.
/// Callers must check this before treating <see cref="LzwData"/> as PDF-`/LZWDecode`-compatible.
/// </param>
/// <param name="Interlaced">
/// Whether the frame's Image Descriptor declares Adam7-style interlacing. GIF's LZW compresses whatever
/// row order it's given — interlacing is a row-reordering applied to the pixel indices before
/// compression, not something LZW itself is aware of — so an interlaced frame's raw LZW bytes decompress
/// into indices in 4-pass order, not top-to-bottom raster order. PDF has no equivalent de-interlace step
/// for image data, so <see cref="LzwData"/> is not usable for direct PDF embedding when this is
/// <see langword="true"/>; fall back to a full decode instead.
/// </param>
/// <param name="Palette">The frame's effective palette (its Local Color Table if present, else the file's Global Color Table), as flat RGB triples.</param>
/// <param name="TransparentColorIndex">The frame's transparent color index from its Graphic Control Extension, or <see langword="null"/> if none was declared — for building a PDF color-key <c>/Mask</c>.</param>
/// <param name="IsAnimated">Whether more than one frame follows in the file. <see cref="LzwData"/> is still only frame 1's; PeachImage does not interpret animation frames here.</param>
/// <param name="LzwData">The frame's LZW-compressed image data, concatenated from its GIF sub-blocks with the sub-block length-prefix framing already stripped — a continuous LZW bitstream, byte-for-byte as it appears in the file.</param>
public readonly record struct GifPassthroughInfo(
    int Width,
    int Height,
    int Left,
    int Top,
    int CanvasWidth,
    int CanvasHeight,
    byte MinCodeSize,
    bool Interlaced,
    byte[] Palette,
    int? TransparentColorIndex,
    bool IsAnimated,
    byte[] LzwData);
