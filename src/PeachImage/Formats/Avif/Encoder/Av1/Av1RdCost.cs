using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// The Lagrangian <c>D + lambda*R</c> rate-distortion cost this encoder's mode/angle/partition search
/// (<c>Av1TileEncoder.ComputeCandidateCost</c>) compares candidates with, replacing the two proxies it used
/// before this: non-lossless candidates were ranked by distortion alone (SSE, no rate term at all), and
/// lossless candidates by a hand-tuned per-coefficient magnitude/log2 proxy that only approximated real bit
/// cost. <see cref="Av1SymbolEncoder.EstimateSymbolCost"/> now gives every candidate a real (not proxied) bit
/// count for its actual quantized residual, straight from the same context-derivation code the real bitstream
/// writer uses (<c>Av1CoefficientWriter.WriteCoeffs</c>, called against an <see cref="Av1TrialSymbolSink"/>) --
/// this type only supplies the other half of the Lagrangian formula: the <c>qindex -> lambda</c> mapping that
/// weighs that real bit count against distortion, and the combine step itself.
/// </summary>
internal static class Av1RdCost
{
    /// <summary>
    /// Scales <see cref="QIndexToLambda"/>'s <c>lambda = LambdaScale * qstep^2</c> curve, the standard
    /// rate-distortion-theory form (the optimal Lagrange multiplier for a quantizer step <c>q</c> grows with
    /// <c>q^2</c>, the same shape real AV1/libaom's own <c>av1_compute_rd_mult</c> uses, though not its exact
    /// constant -- libaom's constant is entangled with its own internal SSE/bit-cost normalization, which this
    /// encoder's simpler proxies (SSE in 8-bit pixel-squared units, bits from <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>'s
    /// literal renormalization-bit count) don't share. Tuned empirically instead, against this repo's own
    /// corpus and this project's size/SSIM-vs-libaom comparison (see the project plan's Phase 1 verification
    /// step) rather than reverse-derived from libaom's constant.
    /// </summary>
    private const double LambdaScale = 0.15 / 256.0;

    /// <summary>
    /// Maps a frame's <c>base_q_idx</c> to the lambda this frame's whole candidate search should weigh real
    /// bit counts by. Lossless (<paramref name="baseQIdx"/> &lt;= 0, AV1's coded-lossless trigger) returns 0:
    /// lossless distortion is always exactly zero once a candidate is really committed (every 4x4
    /// Walsh-Hadamard sub-block reconstructs bit-exactly regardless of which candidate wins), so weighing a
    /// meaningless zero-distortion term would just add noise -- lossless candidates are, correctly, ranked by
    /// real bit count alone.
    /// </summary>
    public static double QIndexToLambda(int baseQIdx)
    {
        if (baseQIdx <= 0)
        {
            return 0.0;
        }

        int acQ = Av1Dequantizer.AcQ(baseQIdx, 8);
        return LambdaScale * acQ * acQ;
    }

    /// <summary>
    /// <c>D + lambda*R</c>: combines a candidate's distortion (<paramref name="sse"/>, spatial-domain sum of
    /// squared error against source) with its real entropy-coded bit count (<paramref name="bits"/>, from
    /// <see cref="Av1TrialSymbolSink.Bits"/>) via <paramref name="lambda"/> (<see cref="QIndexToLambda"/>).
    /// For lossless (<paramref name="lambda"/> == 0), this reduces to <paramref name="sse"/> alone, which is
    /// always 0 for a real lossless residual measured against itself -- callers still pass the real
    /// <paramref name="bits"/> count so lossless search results (already rate-only by construction, per
    /// <see cref="QIndexToLambda"/>'s remarks) aren't silently ignored; see
    /// <c>Av1TileEncoder.ComputeCandidateCost</c>'s lossless branch, which calls this with <paramref name="sse"/>
    /// fixed at 0 and <paramref name="lambda"/> fixed at 1.0 instead, treating bits as the entire cost.
    /// </summary>
    public static long CombineCost(long sse, long bits, double lambda) => sse + (long)Math.Round(lambda * bits);
}
