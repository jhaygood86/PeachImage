namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>VP8L's four optional pre-processing transforms, identified by their 2-bit bitstream type code — verified against libwebp's <c>VP8LImageTransformType</c> (<c>src/webp/format_constants.h</c>).</summary>
internal enum Vp8LTransformType
{
    Predictor = 0,
    CrossColor = 1,
    SubtractGreen = 2,
    ColorIndexing = 3,
}

/// <summary>
/// One parsed transform declaration. <see cref="Xsize"/>/<see cref="Ysize"/> record the pixel buffer's
/// dimensions <em>at the point this transform was read</em> (before this transform's own possible effect on
/// the running "current width" the encoder threads through transform declarations — see
/// <see cref="Vp8LTransformReader"/>), which is exactly the width/height its inverse must operate over when
/// transforms are unwound in reverse declaration order.
/// </summary>
internal sealed class Vp8LTransform
{
    public required Vp8LTransformType Type { get; init; }

    public required int Xsize { get; init; }

    public required int Ysize { get; init; }

    /// <summary>Tile size (as a power-of-two exponent) for <see cref="Vp8LTransformType.Predictor"/>/<see cref="Vp8LTransformType.CrossColor"/>; the color-indexing packing exponent (0-3) for <see cref="Vp8LTransformType.ColorIndexing"/>; unused for <see cref="Vp8LTransformType.SubtractGreen"/>.</summary>
    public int Bits { get; init; }

    /// <summary>The decoded parameter sub-image: per-tile predictor modes or color-transform multipliers, or (after expansion) the color-indexing palette. <see langword="null"/> for <see cref="Vp8LTransformType.SubtractGreen"/>, which needs no parameters.</summary>
    public uint[]? Data { get; init; }
}

/// <summary>Small ARGB pixel-arithmetic helpers shared by more than one transform's inverse.</summary>
internal static class Vp8LPixelMath
{
    /// <summary>
    /// Adds two packed ARGB values channel-by-channel, mod 256, with no carry between channels — VP8L's
    /// <c>VP8LAddPixels</c>. Splits the odd/even byte pairs apart before adding so a channel's overflow can't
    /// bleed into its neighbor, exactly the operation the predictor transform's inverse combine step and the
    /// color-indexing palette's cumulative-delta expansion both need.
    /// </summary>
    public static uint AddWrapping(uint a, uint b)
    {
        uint alphaAndGreen = (a & 0xFF00FF00u) + (b & 0xFF00FF00u);
        uint redAndBlue = (a & 0x00FF00FFu) + (b & 0x00FF00FFu);
        return (alphaAndGreen & 0xFF00FF00u) | (redAndBlue & 0x00FF00FFu);
    }

    /// <summary>
    /// Encoder-only: subtracts two packed ARGB values channel-by-channel, mod 256, with no borrow between
    /// channels — the inverse of <see cref="AddWrapping"/>, needed by the predictor and color-indexing
    /// transforms' forward (encode) directions.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="AddWrapping"/>'s two-packed-lane-at-a-time trick: that trick relies on a
    /// carry into an all-zero "gutter" byte (the unused channel between two packed lanes) dying out after
    /// setting exactly one bit, since <c>0 + 0 + carry</c> never itself overflows. Subtraction has no such
    /// property — a borrow into an all-zero gutter byte (<c>0 - 0 - borrow</c>) always re-borrows, so it
    /// ripples all the way through the gutter into the next real channel, silently corrupting it. Explicit
    /// per-byte subtraction sidesteps that asymmetry entirely.
    /// </remarks>
    public static uint SubtractWrapping(uint a, uint b)
    {
        byte r0 = (byte)((byte)a - (byte)b);
        byte r1 = (byte)((byte)(a >> 8) - (byte)(b >> 8));
        byte r2 = (byte)((byte)(a >> 16) - (byte)(b >> 16));
        byte r3 = (byte)((byte)(a >> 24) - (byte)(b >> 24));
        return r0 | ((uint)r1 << 8) | ((uint)r2 << 16) | ((uint)r3 << 24);
    }
}
