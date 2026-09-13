using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1.IntraPrediction;

/// <summary>
/// SIMD tier of <see cref="IAv1PaethKernel"/>, ported from libaom's own <c>aom_paeth_predictor_*_ssse3</c>/
/// <c>paeth_8x1_pred</c> (<c>aom_dsp/x86/intrapred_ssse3.c</c>): per row, broadcast that row's
/// <c>leftCol[row]</c> and the shared <c>aboveMinus1</c> corner sample, then process the row's columns
/// <see cref="Lanes"/> at a time against the (non-broadcast) above-row vector using branchless
/// compare/select instead of libaom's own 16-bit-lane packed compare -- this port uses <see cref="int"/>
/// lanes throughout (matching this project's own <see cref="int"/>-typed pixel buffers end to end, including
/// for the high-bit-depth values <see cref="ScalarAv1PaethKernel"/> already supports) rather than libaom's
/// 8-bit-only packed representation, so the comparison/select mechanism is the same but the lane width and
/// type differ.
/// </summary>
internal sealed class Vector128Av1PaethKernel : IAv1PaethKernel
{
    private const int Lanes = 4;

    public void Apply(Span<int> pred, int w, int h, ReadOnlySpan<int> aboveRow, ReadOnlySpan<int> leftCol, int aboveMinus1)
    {
        var topLeftVec = Vector128.Create(aboveMinus1);

        for (int i = 0; i < h; i++)
        {
            var leftVec = Vector128.Create(leftCol[i]);
            Span<int> predRow = pred.Slice(i * w, w);

            int j = 0;
            for (; j + Lanes <= w; j += Lanes)
            {
                var topVec = Vector128.Create(aboveRow.Slice(j, Lanes));
                var baseVec = topVec + leftVec - topLeftVec;

                var pLeft = Vector128.Abs(baseVec - leftVec);
                var pTop = Vector128.Abs(baseVec - topVec);
                var pTopLeft = Vector128.Abs(baseVec - topLeftVec);

                var mask1 = Vector128.LessThanOrEqual(pLeft, pTop) & Vector128.LessThanOrEqual(pLeft, pTopLeft);
                var mask2 = Vector128.LessThanOrEqual(pTop, pTopLeft);

                var result = Vector128.ConditionalSelect(mask1, leftVec, Vector128.ConditionalSelect(mask2, topVec, topLeftVec));
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
