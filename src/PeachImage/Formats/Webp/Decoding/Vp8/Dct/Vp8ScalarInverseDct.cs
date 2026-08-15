namespace PeachImage.Formats.Webp.Decoding.Vp8.Dct;

/// <summary>
/// VP8's 4x4 inverse DCT (RFC 6386 section 14.3), fused with adding the residual to an existing prediction and
/// clamping to [0,255] in one pass - matching libwebp's <c>src/dsp/dec.c</c> <c>TransformOne_C</c> exactly
/// (two-pass butterfly: vertical then horizontal, both using the same <see cref="Vp8InverseTransformConstants"/>
/// scaling), plus a DC-only fast path (<c>TransformDC_C</c>) for the common case where only the DC coefficient
/// survived quantization.
/// </summary>
internal static class Vp8ScalarInverseDct
{
    /// <summary>
    /// Adds the inverse-transformed <paramref name="coefficients"/> (16 entries, natural raster order) to the
    /// existing 4x4 block of already-predicted pixels at <paramref name="dst"/>[<paramref name="offset"/>]
    /// (row stride <paramref name="stride"/>), clamping each result to [0,255].
    /// </summary>
    public static void TransformAndAdd(ReadOnlySpan<short> coefficients, Span<byte> dst, int offset, int stride)
    {
        bool anyAc = false;
        for (int i = 1; i < 16; i++)
        {
            if (coefficients[i] != 0)
            {
                anyAc = true;
                break;
            }
        }

        if (!anyAc)
        {
            if (coefficients[0] == 0)
            {
                return;
            }

            TransformDcOnlyAndAdd(coefficients, dst, offset, stride);
            return;
        }

        TransformFullAndAdd(coefficients, dst, offset, stride);
    }

    /// <summary>
    /// Adds a block whose only surviving coefficient is DC — <c>TransformDC_C</c>. The inverse transform of a
    /// DC-only block is a constant, so this adds that constant to all 16 pixels instead of running the
    /// butterflies.
    /// </summary>
    /// <remarks>
    /// Split out so <see cref="Vp8FrameDecoder"/> can select it directly. The coefficient decoder already knows
    /// which of the three cases (nothing, DC only, full) a block is in, from the scan position it stopped at;
    /// having <see cref="TransformAndAdd"/> rediscover that by scanning 15 <see cref="short"/>s per block cost a
    /// measurable share of decode, since it ran for all 24 blocks of every macroblock including the majority
    /// that quantized to nothing at all.
    /// </remarks>
    public static void TransformDcOnlyAndAdd(ReadOnlySpan<short> coefficients, Span<byte> dst, int offset, int stride)
    {
        int dc = (coefficients[0] + 4) >> 3;
        for (int y = 0; y < 4; y++)
        {
            int rowOffset = offset + (y * stride);
            for (int x = 0; x < 4; x++)
            {
                dst[rowOffset + x] = ClipAdd(dst[rowOffset + x], dc);
            }
        }
    }

    /// <summary>Runs the full two-pass butterfly — <see cref="TransformAndAdd"/>'s general case, for a block known to carry at least one AC coefficient.</summary>
    public static void TransformFullAndAdd(ReadOnlySpan<short> coefficients, Span<byte> dst, int offset, int stride)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int in0 = coefficients[i];
            int in4 = coefficients[4 + i];
            int in8 = coefficients[8 + i];
            int in12 = coefficients[12 + i];

            int a = in0 + in8;
            int b = in0 - in8;
            int c = Vp8InverseTransformConstants.Mul2(in4) - Vp8InverseTransformConstants.Mul1(in12);
            int d = Vp8InverseTransformConstants.Mul1(in4) + Vp8InverseTransformConstants.Mul2(in12);

            tmp[(i * 4) + 0] = a + d;
            tmp[(i * 4) + 1] = b + c;
            tmp[(i * 4) + 2] = b - c;
            tmp[(i * 4) + 3] = a - d;
        }

        for (int i = 0; i < 4; i++)
        {
            int t0 = tmp[(0 * 4) + i];
            int t4 = tmp[(1 * 4) + i];
            int t8 = tmp[(2 * 4) + i];
            int t12 = tmp[(3 * 4) + i];

            int dc = t0 + 4;
            int a = dc + t8;
            int b = dc - t8;
            int c = Vp8InverseTransformConstants.Mul2(t4) - Vp8InverseTransformConstants.Mul1(t12);
            int d = Vp8InverseTransformConstants.Mul1(t4) + Vp8InverseTransformConstants.Mul2(t12);

            int rowOffset = offset + (i * stride);
            dst[rowOffset + 0] = ClipAdd(dst[rowOffset + 0], (a + d) >> 3);
            dst[rowOffset + 1] = ClipAdd(dst[rowOffset + 1], (b + c) >> 3);
            dst[rowOffset + 2] = ClipAdd(dst[rowOffset + 2], (b - c) >> 3);
            dst[rowOffset + 3] = ClipAdd(dst[rowOffset + 3], (a - d) >> 3);
        }
    }

    private static byte ClipAdd(byte baseValue, int delta)
    {
        int v = baseValue + delta;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}