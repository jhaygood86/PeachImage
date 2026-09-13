namespace PeachImage.Formats.Avif.Decoding.Av1.IntraPrediction;

/// <summary>
/// Scalar reference tier of <see cref="IAv1PaethKernel"/> -- the same per-pixel nested-ternary choice
/// <see cref="Av1IntraPrediction"/> used before this kernel existed, moved here verbatim so every tier is
/// verifiable against the identical reference.
/// </summary>
internal sealed class ScalarAv1PaethKernel : IAv1PaethKernel
{
    public void Apply(Span<int> pred, int w, int h, ReadOnlySpan<int> aboveRow, ReadOnlySpan<int> leftCol, int aboveMinus1)
    {
        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                int baseVal = aboveRow[j] + leftCol[i] - aboveMinus1;
                int pLeft = Math.Abs(baseVal - leftCol[i]);
                int pTop = Math.Abs(baseVal - aboveRow[j]);
                int pTopLeft = Math.Abs(baseVal - aboveMinus1);

                pred[(i * w) + j] = pLeft <= pTop && pLeft <= pTopLeft ? leftCol[i] : pTop <= pTopLeft ? aboveRow[j] : aboveMinus1;
            }
        }
    }
}
