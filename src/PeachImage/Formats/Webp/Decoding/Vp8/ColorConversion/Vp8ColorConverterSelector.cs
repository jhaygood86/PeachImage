namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>
/// Selects the best available <see cref="IVp8ColorConverter"/> implementation, mirroring the
/// <c>Vector256 &gt; Vector128 &gt; scalar</c> hardware-tier selector pattern used elsewhere in this codebase
/// (e.g. Jpeg's <c>ColorConverterSelector</c>). Only the scalar tier is implemented for this initial VP8 decode
/// pass; a SIMD tier is a reasonable follow-up but was left out here to keep the first implementation's
/// correctness easy to verify.
/// </summary>
internal static class Vp8ColorConverterSelector
{
    public static IVp8ColorConverter Select() => new Vp8ScalarColorConverter();
}