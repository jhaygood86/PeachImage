using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1.IntraPrediction;

/// <summary>
/// AVX2-width tier of <see cref="IAv1PaethKernel"/>, same mechanism as <see cref="Vector128Av1PaethKernel"/>
/// (see its remarks) but processing <see cref="Lanes"/> columns per row at once -- matching libaom's own real
/// <c>aom_paeth_predictor_*_avx2</c>/<c>paeth_pred</c> (<c>aom_dsp/x86/intrapred_avx2.c</c>), which likewise
/// widens the same per-row broadcast-and-compare mechanism to 32 bytes rather than using a fundamentally
/// different algorithm at this width. Real width gain here (unlike
/// <see cref="Encoder.Av1.IntraModel.Vector128Av1Hadamard4x4Kernel"/>'s fixed 4x4 case): Paeth's row width scales with
/// the real AV1 block width (4 to 64), so double-width lanes process twice as many columns per iteration for
/// every block <see cref="Lanes"/> or wider.
/// </summary>
internal sealed class Vector256Av1PaethKernel : IAv1PaethKernel
{
    private const int Lanes = 8;

    public void Apply(Span<int> pred, int w, int h, ReadOnlySpan<int> aboveRow, ReadOnlySpan<int> leftCol, int aboveMinus1)
    {
        var topLeftVec = Vector256.Create(aboveMinus1);

        for (int i = 0; i < h; i++)
        {
            var leftVec = Vector256.Create(leftCol[i]);
            Span<int> predRow = pred.Slice(i * w, w);

            int j = 0;
            for (; j + Lanes <= w; j += Lanes)
            {
                var topVec = Vector256.Create(aboveRow.Slice(j, Lanes));
                var baseVec = topVec + leftVec - topLeftVec;

                var pLeft = Vector256.Abs(baseVec - leftVec);
                var pTop = Vector256.Abs(baseVec - topVec);
                var pTopLeft = Vector256.Abs(baseVec - topLeftVec);

                var mask1 = Vector256.LessThanOrEqual(pLeft, pTop) & Vector256.LessThanOrEqual(pLeft, pTopLeft);
                var mask2 = Vector256.LessThanOrEqual(pTop, pTopLeft);

                var result = Vector256.ConditionalSelect(mask1, leftVec, Vector256.ConditionalSelect(mask2, topVec, topLeftVec));
                result.CopyTo(predRow.Slice(j, Lanes));
            }

            for (; j < w; j++)
            {
                int top = aboveRow[j];
                int baseVal = top + leftCol[i] - aboveMinus1;
                int pLeft = Math.Abs(baseVal - leftCol[i]);
                int pTop = Math.Abs(baseVal - top);
                int pTopLeft = Math.Abs(baseVal - aboveMinus1);

                predRow[j] = pLeft <= pTop && pLeft <= pTopLeft ? leftCol[i] : pTop <= pTopLeft ? top : aboveMinus1;
            }
        }
    }
}
