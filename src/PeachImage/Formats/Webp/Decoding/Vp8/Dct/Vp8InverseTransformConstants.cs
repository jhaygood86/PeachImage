namespace PeachImage.Formats.Webp.Decoding.Vp8.Dct;

/// <summary>
/// Fixed-point constants for VP8's 4x4 inverse DCT (RFC 6386 section 14.3), matching libwebp's
/// <c>src/dsp/dsp.h</c> <c>WEBP_TRANSFORM_AC3_C1</c>/<c>WEBP_TRANSFORM_AC3_C2</c>/<c>_MUL1</c>/<c>_MUL2</c>.
/// These come from <c>sqrt(2) * cos(pi/8)</c> and <c>sqrt(2) * sin(pi/8)</c> scaled to Q16 fixed point, biased
/// so that <see cref="Mul1"/> can be computed as an add instead of an extra multiply.
/// </summary>
internal static class Vp8InverseTransformConstants
{
    public const int C1 = 20091;
    public const int C2 = 35468;

    public static int Mul1(int v) => v + ((v * C1) >> 16);

    public static int Mul2(int v) => (v * C2) >> 16;
}