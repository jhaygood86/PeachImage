using System.Runtime.CompilerServices;

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
    /// <summary>Y's coefficient, shared by all three output channels.</summary>
    internal const int YCoefficient = 19077;

    /// <summary>V's coefficient for red.</summary>
    internal const int VToRed = 26149;

    /// <summary>U's coefficient for green (subtracted).</summary>
    internal const int UToGreen = 6419;

    /// <summary>V's coefficient for green (subtracted).</summary>
    internal const int VToGreen = 13320;

    /// <summary>U's coefficient for blue.</summary>
    internal const int UToBlue = 33050;

    /// <summary>Constant bias for red.</summary>
    internal const int RedBias = -14234;

    /// <summary>Constant bias for green.</summary>
    internal const int GreenBias = 8708;

    /// <summary>Constant bias for blue.</summary>
    internal const int BlueBias = -17685;

    /// <inheritdoc/>
    public void ConvertRow(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> rgb, int width) =>
        ConvertRemainder(y, u, v, rgb, 0, width);

    /// <summary>Converts <paramref name="width"/> minus <paramref name="start"/> samples one at a time — the shared tail of every vectorized tier, and the whole of this one.</summary>
    internal static void ConvertRemainder(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> rgb, int start, int width)
    {
        for (int x = start; x < width; x++)
        {
            ConvertPixel(y[x], u[x], v[x], rgb.Slice(x * 3, 3));
        }
    }

    /// <summary>Converts a single sample triple. Kept as the readable definition of the arithmetic every tier reproduces, and as those tiers' test oracle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ConvertPixel(byte y, byte u, byte v, Span<byte> rgb)
    {
        rgb[0] = (byte)ClipToByteAfterShift(MultHi(y, YCoefficient) + MultHi(v, VToRed) + RedBias);
        rgb[1] = (byte)ClipToByteAfterShift(MultHi(y, YCoefficient) - MultHi(u, UToGreen) - MultHi(v, VToGreen) + GreenBias);
        rgb[2] = (byte)ClipToByteAfterShift(MultHi(y, YCoefficient) + MultHi(u, UToBlue) + BlueBias);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    /// <summary>Right-shifts by the YUV_FIX2 (6) descale and clamps to [0,255], matching libwebp's <c>VP8Clip8</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClipToByteAfterShift(int v)
    {
        int shifted = v >> 6;
        return shifted < 0 ? 0 : shifted > 255 ? 255 : shifted;
    }
}
