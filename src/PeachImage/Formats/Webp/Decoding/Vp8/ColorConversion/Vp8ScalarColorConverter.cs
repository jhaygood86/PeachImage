namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>
/// Studio/limited-range fixed-point YUV-&gt;RGB conversion (BT.601), matching libwebp's <c>src/dsp/yuv.h</c>
/// <c>VP8YuvToRgb</c> exactly - not this codebase's JPEG <c>ScalarColorConverter</c>, which uses full-range
/// floating-point BT.601 coefficients and would produce visibly washed-out color on VP8 pixels. The bias
/// constants below already bake in the studio-range rescale (Y in [16,235], U/V in [16,240] -&gt; full [0,255]
/// output), so raw 0-255 sample bytes are fed in directly with no separate -16/-128 offset step.
/// </summary>
/// <remarks>
/// <c>MultHi(v, c) = (v * c) &gt;&gt; 8</c> emulates libwebp's <c>_mm_mulhi_epu16</c>-based fixed point (8 fractional
/// bits kept before the final &gt;&gt;6 descale in <c>Clip8</c>). Constants transcribed verbatim from and
/// cross-checked against <c>src/dsp/yuv.h</c>: R = 19077.y + 26149.v - 14234; G = 19077.y - 6419.u - 13320.v +
/// 8708; B = 19077.y + 33050.u - 17685 (each right-shifted by 6 via <c>Clip8</c>, which also clamps to
/// [0,255]).
/// </remarks>
internal sealed class Vp8ScalarColorConverter : IVp8ColorConverter
{
    public void Convert(byte y, byte u, byte v, Span<byte> rgb)
    {
        rgb[0] = (byte)ClipToByteAfterShift(MultHi(y, 19077) + MultHi(v, 26149) - 14234);
        rgb[1] = (byte)ClipToByteAfterShift(MultHi(y, 19077) - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
        rgb[2] = (byte)ClipToByteAfterShift(MultHi(y, 19077) + MultHi(u, 33050) - 17685);
    }

    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    /// <summary>Right-shifts by the YUV_FIX2 (6) descale and clamps to [0,255], matching libwebp's <c>VP8Clip8</c>.</summary>
    private static int ClipToByteAfterShift(int v)
    {
        int shifted = v >> 6;
        return shifted < 0 ? 0 : shifted > 255 ? 255 : shifted;
    }
}