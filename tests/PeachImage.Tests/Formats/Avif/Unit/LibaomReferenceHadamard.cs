namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Direct, independent transcription of libaom's own <c>aom_hadamard_4x4_c</c>/<c>hadamard_col4</c>/
/// <c>aom_satd_c</c> (<c>aom_dsp/avg.c</c>) -- the real SATD-model-RD transform PeachImage's own
/// <see cref="PeachImage.Formats.Avif.Encoder.Av1.Av1IntraModelRdPruner"/> ports, kept deliberately
/// separate so a test comparing the two is a genuine check against libaom's own real algorithm, not a
/// self-consistency check against the port itself. Reproduces libaom's own <c>int16_t</c> intermediate
/// truncation exactly (<see cref="Av1IntraModelRdPruner.Hadamard4x4"/> uses <see langword="int"/>
/// intermediates instead -- provably equivalent for every value this transform's own real dynamic-range
/// comments in <c>avg.c</c> allow, since those stay well within <see langword="short"/> range, but this
/// reference keeps the narrower type so the test genuinely exercises that equivalence rather than assuming
/// it).
/// </summary>
internal static class LibaomReferenceHadamard
{
    public static void Hadamard4x4(ReadOnlySpan<short> srcDiff, int srcStride, Span<int> coeff)
    {
        Span<short> buffer = stackalloc short[16];
        Span<short> buffer2 = stackalloc short[16];

        for (int idx = 0; idx < 4; idx++)
        {
            HadamardCol4(srcDiff, idx, srcStride, buffer, idx * 4);
        }

        for (int idx = 0; idx < 4; idx++)
        {
            HadamardCol4(buffer, idx, 4, buffer2, idx * 4);
        }

        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                coeff[(i * 4) + j] = buffer2[(j * 4) + i];
            }
        }
    }

    private static void HadamardCol4(ReadOnlySpan<short> src, int srcOffset, int stride, Span<short> dst, int dstOffset)
    {
        short b0 = (short)((src[srcOffset + (0 * stride)] + src[srcOffset + (1 * stride)]) >> 1);
        short b1 = (short)((src[srcOffset + (0 * stride)] - src[srcOffset + (1 * stride)]) >> 1);
        short b2 = (short)((src[srcOffset + (2 * stride)] + src[srcOffset + (3 * stride)]) >> 1);
        short b3 = (short)((src[srcOffset + (2 * stride)] - src[srcOffset + (3 * stride)]) >> 1);

        dst[dstOffset + 0] = (short)(b0 + b2);
        dst[dstOffset + 1] = (short)(b1 + b3);
        dst[dstOffset + 2] = (short)(b0 - b2);
        dst[dstOffset + 3] = (short)(b1 - b3);
    }

    public static long Satd(ReadOnlySpan<int> coeff)
    {
        long satd = 0;
        foreach (int c in coeff)
        {
            satd += Math.Abs(c);
        }

        return satd;
    }
}
