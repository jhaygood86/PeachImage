namespace PeachImage.Formats.Avif.Encoder.Av1.IntraModel;

/// <summary>
/// Scalar reference tier of <see cref="IAv1Hadamard4x4Kernel"/> -- the same column-then-row butterfly plus
/// final transpose <see cref="Av1IntraModelRdPruner"/> used before this kernel existed, moved here verbatim
/// so every tier is verifiable against the identical reference.
/// </summary>
internal sealed class ScalarAv1Hadamard4x4Kernel : IAv1Hadamard4x4Kernel
{
    public void Apply(ReadOnlySpan<int> srcDiff, int srcStride, Span<int> coeff)
    {
        Span<int> buffer = stackalloc int[16];
        Span<int> buffer2 = stackalloc int[16];

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

    /// <summary><c>hadamard_col4</c> (<c>aom_dsp/avg.c</c>) -- a 4-point butterfly with an intermediate right-shift-by-1 (C's arithmetic right shift on a signed value, reproduced exactly by C#'s own <c>&gt;&gt;</c> on <c>int</c>).</summary>
    private static void HadamardCol4(ReadOnlySpan<int> src, int srcOffset, int stride, Span<int> dst, int dstOffset)
    {
        int b0 = (src[srcOffset + (0 * stride)] + src[srcOffset + (1 * stride)]) >> 1;
        int b1 = (src[srcOffset + (0 * stride)] - src[srcOffset + (1 * stride)]) >> 1;
        int b2 = (src[srcOffset + (2 * stride)] + src[srcOffset + (3 * stride)]) >> 1;
        int b3 = (src[srcOffset + (2 * stride)] - src[srcOffset + (3 * stride)]) >> 1;

        dst[dstOffset + 0] = b0 + b2;
        dst[dstOffset + 1] = b1 + b3;
        dst[dstOffset + 2] = b0 - b2;
        dst[dstOffset + 3] = b1 - b3;
    }
}
