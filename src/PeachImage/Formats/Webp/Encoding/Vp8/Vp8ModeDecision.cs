using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Intra prediction mode selection: for each candidate mode, predicts directly into the working reconstruction
/// plane (reusing <see cref="Vp8IntraPredictionWholeBlock"/>/<see cref="Vp8IntraPrediction4x4"/> unchanged —
/// they only ever read already-finalized neighbor pixels above/left of the block being predicted, never the
/// block's own current contents), scores it against the source block by sum-of-absolute-differences, and keeps
/// the cheapest. This is a v1 distortion-only decision (no rate-distortion Lagrangian term weighing actual
/// entropy-coding cost) — full RDO needs a working bit-cost estimator, a materially larger piece of machinery;
/// SAD-only selection is what most first-working intra encoders converge to, and is the natural place to add an
/// RD term later without restructuring the surrounding pipeline.
/// </summary>
internal static class Vp8ModeDecision
{
    private static readonly int[] WholeBlockModes =
    [
        Vp8PredictionModes.DcPred,
        Vp8PredictionModes.VPred,
        Vp8PredictionModes.HPred,
        Vp8PredictionModes.TmPred,
    ];

    /// <summary>
    /// Tries DC/V/H/TM at <paramref name="origin"/> (16x16 luma or 8x8 chroma, per <paramref name="size"/>),
    /// leaves the winning mode's prediction committed in <paramref name="recon"/>, and returns that mode.
    /// </summary>
    public static int SelectWholeBlockMode(
        Span<byte> recon, int origin, int stride, int size, bool hasAbove, bool hasLeft,
        ReadOnlySpan<byte> source, int sourceOrigin, int sourceStride,
        out int bestSad)
    {
        int bestMode = WholeBlockModes[0];
        bestSad = int.MaxValue;
        int lastTried = bestMode;

        foreach (int mode in WholeBlockModes)
        {
            Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(mode, recon, origin, stride, size, hasAbove, hasLeft);
            lastTried = mode;
            int sad = Sad(source, sourceOrigin, sourceStride, recon, origin, stride, size);
            if (sad < bestSad)
            {
                bestSad = sad;
                bestMode = mode;
            }
        }

        // The block holds the last candidate's prediction; only redo it if a different mode won.
        if (bestMode != lastTried)
        {
            Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(bestMode, recon, origin, stride, size, hasAbove, hasLeft);
        }

        return bestMode;
    }

    /// <summary>
    /// Tries all 10 4x4 B_PRED modes at <paramref name="origin"/>, leaves the winning mode's prediction
    /// committed in <paramref name="recon"/>, and returns that mode. Callers evaluating a real (not just a
    /// decision-heuristic) B_PRED subblock must call this only once <paramref name="recon"/>'s above/left
    /// neighbors already hold real reconstructed pixels (i.e. in the same raster-order, causal sequence
    /// <see cref="Decoding.Vp8.Vp8FrameDecoder"/> reconstructs subblocks in), since <see cref="Vp8IntraPrediction4x4"/>
    /// reads them directly.
    /// </summary>
    public static int SelectSubblockMode(
        Span<byte> recon, int origin, int stride, ReadOnlySpan<byte> aboveRight,
        ReadOnlySpan<byte> source, int sourceOrigin, int sourceStride,
        out int bestSad)
    {
        int bestMode = 0;
        bestSad = int.MaxValue;

        for (int mode = 0; mode < Vp8PredictionModes.NumBModes; mode++)
        {
            Vp8IntraPrediction4x4.Predict(mode, recon, origin, stride, aboveRight);
            int sad = Sad(source, sourceOrigin, sourceStride, recon, origin, stride, 4);
            if (sad < bestSad)
            {
                bestSad = sad;
                bestMode = mode;
            }
        }

        Vp8IntraPrediction4x4.Predict(bestMode, recon, origin, stride, aboveRight);
        return bestMode;
    }

    /// <summary>
    /// Sum of absolute differences over a <paramref name="size"/> x <paramref name="size"/> block. 16-, 8- and
    /// 4-wide blocks (every size the encoder asks for) run on <see cref="Vector128"/>: per-byte
    /// <c>max(a, b) - min(a, b)</c> is exact, and the row sums are accumulated in 16-bit lanes (at most
    /// 2 * 255 * 16 rows per lane) before one final horizontal add, so the result is the same integer as the
    /// scalar loop and mode decisions (strict <c>&lt;</c> comparisons) are unchanged.
    /// </summary>
    private static int Sad(ReadOnlySpan<byte> a, int aOffset, int aStride, ReadOnlySpan<byte> b, int bOffset, int bStride, int size)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            switch (size)
            {
                case 16:
                    return Sad16(a, aOffset, aStride, b, bOffset, bStride);
                case 8:
                    return Sad8(a, aOffset, aStride, b, bOffset, bStride);
                case 4:
                    return Sad4(a, aOffset, aStride, b, bOffset, bStride);
            }
        }

        int sad = 0;
        for (int y = 0; y < size; y++)
        {
            int ao = aOffset + (y * aStride);
            int bo = bOffset + (y * bStride);
            for (int x = 0; x < size; x++)
            {
                sad += Math.Abs(a[ao + x] - b[bo + x]);
            }
        }

        return sad;
    }

    private static int Sad16(ReadOnlySpan<byte> a, int aOffset, int aStride, ReadOnlySpan<byte> b, int bOffset, int bStride)
    {
        var acc = Vector128<ushort>.Zero;
        for (int y = 0; y < 16; y++)
        {
            var diff = AbsDiff(Load16(a, aOffset + (y * aStride)), Load16(b, bOffset + (y * bStride)));
            acc += Vector128.WidenLower(diff) + Vector128.WidenUpper(diff);
        }

        return HorizontalSum(acc);
    }

    private static int Sad8(ReadOnlySpan<byte> a, int aOffset, int aStride, ReadOnlySpan<byte> b, int bOffset, int bStride)
    {
        var acc = Vector128<ushort>.Zero;
        for (int y = 0; y < 8; y++)
        {
            var diff = AbsDiff(Load8(a, aOffset + (y * aStride)), Load8(b, bOffset + (y * bStride)));
            acc += Vector128.WidenLower(diff) + Vector128.WidenUpper(diff);
        }

        return HorizontalSum(acc);
    }

    /// <summary>A 4x4 block fits one vector: gather its four 4-byte rows, then a single absolute-difference.</summary>
    private static int Sad4(ReadOnlySpan<byte> a, int aOffset, int aStride, ReadOnlySpan<byte> b, int bOffset, int bStride)
    {
        var diff = AbsDiff(Load4Rows(a, aOffset, aStride), Load4Rows(b, bOffset, bStride));
        return HorizontalSum(Vector128.WidenLower(diff) + Vector128.WidenUpper(diff));
    }

    private static int HorizontalSum(Vector128<ushort> lanes) =>
        (int)Vector128.Sum(Vector128.WidenLower(lanes) + Vector128.WidenUpper(lanes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AbsDiff(Vector128<byte> x, Vector128<byte> y) => Vector128.Max(x, y) - Vector128.Min(x, y);

    private static Vector128<byte> Load16(ReadOnlySpan<byte> span, int offset)
    {
        _ = span[offset + 15];
        return Vector128.LoadUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), offset));
    }

    /// <summary>Eight bytes in the low half, zeros above (which contribute nothing to the sum).</summary>
    private static Vector128<byte> Load8(ReadOnlySpan<byte> span, int offset) =>
        Vector128.CreateScalar(MemoryMarshal.Read<ulong>(span.Slice(offset, 8))).AsByte();

    private static Vector128<byte> Load4Rows(ReadOnlySpan<byte> span, int origin, int stride) =>
        Vector128.Create(
            MemoryMarshal.Read<uint>(span.Slice(origin, 4)),
            MemoryMarshal.Read<uint>(span.Slice(origin + stride, 4)),
            MemoryMarshal.Read<uint>(span.Slice(origin + (2 * stride), 4)),
            MemoryMarshal.Read<uint>(span.Slice(origin + (3 * stride), 4))).AsByte();
}
