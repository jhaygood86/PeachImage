namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Fixed-point constants for VP8's forward 4x4 DCT (RFC 6386 section 14.3's decode-side transform has a forward
/// counterpart that is not its exact algebraic inverse — libwebp's real encoder pairs a differently-scaled
/// forward transform with the decoder's <c>C1</c>/<c>C2</c> inverse, calibrated as a matched pair rather than
/// derived symbolically). Transcribed verbatim from libwebp's <c>src/dsp/enc.c</c> <c>FTransform_C</c>,
/// cross-checked against the downloaded upstream source. Round-trip accuracy against
/// <see cref="Decoding.Vp8.Dct.Vp8ScalarInverseDct"/> is validated empirically by
/// <c>Vp8ForwardDctTests</c>, not assumed from the arithmetic.
/// </summary>
internal static class Vp8ForwardTransformConstants
{
    /// <summary>First-pass and second-pass rotation coefficient (paired with <see cref="Cos"/>).</summary>
    public const int Sin = 2217;

    /// <summary>First-pass and second-pass rotation coefficient (paired with <see cref="Sin"/>).</summary>
    public const int Cos = 5352;

    /// <summary>First-pass rounding bias for the odd (index 1) output.</summary>
    public const int FirstPassBiasOdd1 = 1812;

    /// <summary>First-pass rounding bias for the odd (index 3) output.</summary>
    public const int FirstPassBiasOdd3 = 937;

    /// <summary>Second-pass rounding bias for the odd (index 1, i.e. output row 4) coefficient, before the final &gt;&gt;16 descale.</summary>
    public const int SecondPassBiasOdd1 = 12000;

    /// <summary>Second-pass rounding bias for the odd (index 3, i.e. output row 12) coefficient, before the final &gt;&gt;16 descale.</summary>
    public const int SecondPassBiasOdd3 = 51000;
}
