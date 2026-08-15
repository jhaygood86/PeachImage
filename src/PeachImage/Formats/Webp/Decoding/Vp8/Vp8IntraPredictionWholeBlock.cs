using System.Numerics;

namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// Whole-block intra prediction (RFC 6386 section 12.2): VP8's 16x16 luma modes and 8x8 chroma modes, both
/// sharing the same four-mode set (DC/V/H/TM) and pixel formulas, just at different block sizes.
/// </summary>
/// <remarks>
/// All methods operate directly on a macroblock-aligned plane buffer that has been padded with one extra border
/// row above (filled with 127) and one extra border column to the left (filled with 129) before decoding starts
/// - see <see cref="Vp8FrameDecoder"/> for the padding setup. This lets <see cref="PredictVertical"/>,
/// <see cref="PredictHorizontal"/>, and <see cref="PredictTrueMotion"/> simply read whatever is physically above
/// or to the left of <c>origin</c> without any conditional logic: at the frame's real top row /
/// left column, that memory already holds the correct synthetic border value. Only <see cref="PredictDc"/> needs
/// explicit availability flags, because unlike the other three modes its formula (not just its inputs) changes
/// when a neighbor is missing (RFC 6386 section 12.2, libwebp's <c>DC16_C</c>/<c>DC16NoTop_C</c>/
/// <c>DC16NoLeft_C</c>/<c>DC16NoTopLeft_C</c> family).
/// </remarks>
internal static class Vp8IntraPredictionWholeBlock
{
    public static void PredictDc(Span<byte> plane, int origin, int stride, int size, bool hasAbove, bool hasLeft)
    {
        int dc;
        if (hasAbove && hasLeft)
        {
            int sum = 0;
            for (int i = 0; i < size; i++)
            {
                sum += plane[origin - stride + i] + plane[origin + (i * stride) - 1];
            }

            int shift = BitOperations.Log2((uint)size) + 1;
            dc = (sum + size) >> shift;
        }
        else if (hasAbove)
        {
            int sum = 0;
            for (int i = 0; i < size; i++)
            {
                sum += plane[origin - stride + i];
            }

            int shift = BitOperations.Log2((uint)size);
            dc = (sum + (size >> 1)) >> shift;
        }
        else if (hasLeft)
        {
            int sum = 0;
            for (int i = 0; i < size; i++)
            {
                sum += plane[origin + (i * stride) - 1];
            }

            int shift = BitOperations.Log2((uint)size);
            dc = (sum + (size >> 1)) >> shift;
        }
        else
        {
            dc = 128;
        }

        byte value = (byte)dc;
        for (int y = 0; y < size; y++)
        {
            plane.Slice(origin + (y * stride), size).Fill(value);
        }
    }

    public static void PredictVertical(Span<byte> plane, int origin, int stride, int size)
    {
        ReadOnlySpan<byte> above = plane.Slice(origin - stride, size);
        for (int y = 0; y < size; y++)
        {
            above.CopyTo(plane.Slice(origin + (y * stride), size));
        }
    }

    public static void PredictHorizontal(Span<byte> plane, int origin, int stride, int size)
    {
        for (int y = 0; y < size; y++)
        {
            byte value = plane[origin + (y * stride) - 1];
            plane.Slice(origin + (y * stride), size).Fill(value);
        }
    }

    public static void PredictTrueMotion(Span<byte> plane, int origin, int stride, int size)
    {
        int corner = plane[origin - stride - 1];
        for (int y = 0; y < size; y++)
        {
            int left = plane[origin + (y * stride) - 1];
            int rowOrigin = origin + (y * stride);
            for (int x = 0; x < size; x++)
            {
                int above = plane[origin - stride + x];
                plane[rowOrigin + x] = ClipByte(above + left - corner);
            }
        }
    }

    /// <summary>Dispatches to the right whole-block prediction routine for <paramref name="mode"/> (DC_PRED/V_PRED/H_PRED/TM_PRED).</summary>
    public static void PredictModeWholeBlock(int mode, Span<byte> plane, int origin, int stride, int size, bool hasAbove, bool hasLeft)
    {
        switch (mode)
        {
            case Vp8PredictionModes.DcPred:
                PredictDc(plane, origin, stride, size, hasAbove, hasLeft);
                break;
            case Vp8PredictionModes.VPred:
                PredictVertical(plane, origin, stride, size);
                break;
            case Vp8PredictionModes.HPred:
                PredictHorizontal(plane, origin, stride, size);
                break;
            case Vp8PredictionModes.TmPred:
                PredictTrueMotion(plane, origin, stride, size);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown VP8 whole-block prediction mode.");
        }
    }

    internal static byte ClipByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
