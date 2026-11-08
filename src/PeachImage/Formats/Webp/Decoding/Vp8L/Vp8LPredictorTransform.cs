using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Inverts VP8L's predictor (spatial) transform: per tile (side <c>1 &lt;&lt; transform.Bits</c>), one of 14
/// predictor modes (0-13, selected via the tile parameter sub-image's green channel) forecasts each pixel
/// from its already-reconstructed left/top/top-left/top-right neighbors; the encoded residual is then added
/// back, per channel, mod 256. Two positions are unconditionally overridden regardless of what the tile
/// image says (matching libwebp's <c>PredictorInverseTransform_C</c> exactly): pixel (0,0) always uses mode 0
/// (fixed opaque black), and every other pixel in row 0 always uses mode 1 (left) — there is no row above
/// image row 0 for any tile-selected mode to sensibly reference.
/// </summary>
/// <remarks>
/// All neighbor reads use flat, unclamped <c>index +/- 1 +/- width</c> arithmetic against the single flat
/// pixel buffer, deliberately including one quirk this must reproduce exactly to match the reference decoder:
/// at the last column of a row, the "top-right" neighbor read (<c>index - width + 1</c>) is one past the end
/// of the row above, which — because the buffer is contiguous row-major memory — actually lands on the
/// *current* row's own first pixel (already reconstructed by the time it's read, since column 0 of every row
/// is always handled before the rest of that row). This is intentional per the VP8L spec/libwebp, not a bug
/// to guard against; both encoder and decoder rely on it.
/// </remarks>
internal static class Vp8LPredictorTransform
{
    public static void ApplyInverse(Span<uint> pixels, Vp8LTransform transform) =>
        ApplyInverseRows(pixels, 0, transform, 0, transform.Ysize);

    /// <summary>
    /// Inverts rows <paramref name="yStart"/> to <paramref name="yEnd"/> (exclusive) of the image, in place.
    /// <paramref name="pixels"/> holds those rows' residuals starting at index <paramref name="rowsOffset"/>; when
    /// <paramref name="yStart"/> is not 0, the already-reconstructed row <c>yStart - 1</c> must sit in the
    /// <c>Xsize</c> elements immediately before <paramref name="rowsOffset"/>, because the first row's predictors
    /// read it. Reconstructing a whole image is the case <c>rowsOffset = 0, yStart = 0, yEnd = Ysize</c>.
    /// </summary>
    public static void ApplyInverseRows(Span<uint> pixels, int rowsOffset, Vp8LTransform transform, int yStart, int yEnd) =>
        ApplyInverseRows(pixels, rowsOffset, transform, yStart, yEnd, default);

    /// <summary>
    /// As above, but when <paramref name="source"/> is not empty the residuals are read from it (its first row is
    /// row <paramref name="yStart"/>) instead of already being in <paramref name="pixels"/>: each row is copied in just
    /// before it is reconstructed, while it is a few instructions from being used, rather than as a separate bulk pass.
    /// </summary>
    public static void ApplyInverseRows(Span<uint> pixels, int rowsOffset, Vp8LTransform transform, int yStart, int yEnd, ReadOnlySpan<uint> source)
    {
        int width = transform.Xsize;
        int bits = transform.Bits;
        var tileData = transform.Data!;
        int tilesPerRow = Vp8LMetaHuffmanImage.SubSampleSize(width, bits);
        var kernel = Vp8LTransformKernelSelector.Instance;

        int firstFlatRow = yStart;
        if (yStart == 0)
        {
            if (!source.IsEmpty)
            {
                source.Slice(0, width).CopyTo(pixels.Slice(rowsOffset, width));
            }

            // Pixel (0,0): mode 0 (fixed opaque black), unconditionally.
            pixels[rowsOffset] = Vp8LPixelMath.AddWrapping(pixels[rowsOffset], 0xFF000000u);

            // Rest of row 0: mode 1 (left), unconditionally -- sequential, has an intra-row dependency.
            for (int x = 1; x < width; x++)
            {
                pixels[rowsOffset + x] = Vp8LPixelMath.AddWrapping(pixels[rowsOffset + x], pixels[rowsOffset + x - 1]);
            }

            firstFlatRow = 1;
        }

        for (int y = firstFlatRow; y < yEnd; y++)
        {
            int rowBase = rowsOffset + ((y - yStart) * width);
            if (!source.IsEmpty)
            {
                source.Slice((y - yStart) * width, width).CopyTo(pixels.Slice(rowBase, width));
            }

            // First pixel of the row: mode 2 (top), unconditionally.
            pixels[rowBase] = Vp8LPixelMath.AddWrapping(pixels[rowBase], pixels[rowBase - width]);

            int x = 1;
            while (x < width)
            {
                int tileX = x >> bits;
                int mode = (int)((tileData[((y >> bits) * tilesPerRow) + tileX] >> 8) & 0xF);
                int tileEndX = Math.Min((tileX + 1) << bits, width);
                int runLength = tileEndX - x;

                ApplyRun(pixels, kernel, width, rowBase, x, runLength, mode);
                x = tileEndX;
            }
        }
    }

    private static void ApplyRun(Span<uint> pixels, IVp8LTransformKernel kernel, int width, int rowBase, int xStart, int runLength, int mode)
    {
        if (mode == 2)
        {
            // Mode 2 (pure "top") has no intra-run dependency at all -- vectorizable, same operation as
            // Png.Filtering.VectorizedRowFilter.UnfilterUp applied to this run's raw ARGB bytes.
            var rowBytes = MemoryMarshal.Cast<uint, byte>(pixels.Slice(rowBase + xStart, runLength));
            var topBytes = MemoryMarshal.Cast<uint, byte>(pixels.Slice(rowBase - width + xStart, runLength));
            kernel.PredictorTopInverse(rowBytes, topBytes);
            return;
        }

        // `mode` is constant for the whole run, so it is resolved to a concrete predictor here, once, rather
        // than re-tested per pixel. Each Run<T> instantiation is a separate specialization whose Predict call
        // the JIT devirtualizes and inlines, leaving a loop that does only that mode's arithmetic.
        switch (mode)
        {
            case 1: Run<LeftPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 3: Run<TopRightPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 4: Run<TopLeftPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 5: Run<Average3LeftTopTopRightPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 6: Run<Average2LeftTopLeftPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 7: Run<Average2LeftTopPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 8: Run<Average2TopLeftTopPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 9: Run<Average2TopTopRightPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 10: Run<Average4Predictor>(pixels, width, rowBase, xStart, runLength); return;
            case 11:
                if (Sse2.IsSupported)
                {
                    RunSelectSse2(pixels, width, rowBase, xStart, runLength);
                }
                else
                {
                    Run<SelectPredictor>(pixels, width, rowBase, xStart, runLength);
                }

                return;
            case 12: Run<ClampedAddSubtractFullPredictor>(pixels, width, rowBase, xStart, runLength); return;
            case 13: Run<ClampedAddSubtractHalfPredictor>(pixels, width, rowBase, xStart, runLength); return;

            // Mode 0, plus modes 14/15, which the spec assigns no meaning -- see Predict's remarks.
            default: Run<OpaqueBlackPredictor>(pixels, width, rowBase, xStart, runLength); return;
        }
    }

    /// <summary>
    /// Applies one predictor mode across a run of pixels within a single row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carries the neighborhood forward in registers instead of re-reading it. Every neighbor of pixel
    /// <c>i+1</c> is already in hand after pixel <c>i</c>: its "left" is the value just written, its "top-left"
    /// is pixel <c>i</c>'s "top", and its "top" is pixel <c>i</c>'s "top-right". So the loop performs exactly
    /// one load per pixel (the incoming top-right) rather than the four the shared
    /// <see cref="Predict(int, ReadOnlySpan{uint}, int, int)"/> performed — which loaded top-right for every
    /// mode from 3 up, including the many that never use it.
    /// </para>
    /// <para>
    /// Sliding the window this way also preserves the last-column top-right quirk described in this type's
    /// remarks for free: the neighbor row simply advances through contiguous memory, so at the last column it
    /// arrives at the current row's own first pixel exactly as an unclamped <c>index - width + 1</c> read
    /// would. That pixel is written before any run in the row begins and is never rewritten by one, so its
    /// value is the same whether it is read early or late.
    /// </para>
    /// </remarks>
    private static void Run<TPredictor>(Span<uint> pixels, int width, int rowBase, int xStart, int runLength)
        where TPredictor : struct, IPredictorMode
    {
        // The caller only ever produces runs inside a row, at or past column 1, on a row at or past 1. That is
        // what puts every index below -- from `start - width - 1` up to `start + runLength - 1` -- in bounds,
        // and so what makes the unchecked ref arithmetic safe. Checked once here rather than per pixel.
        int start = rowBase + xStart;
        if (xStart < 1 || runLength < 1 || rowBase < width || start + runLength > pixels.Length || xStart + runLength > width)
        {
            throw new WebpDecodingException("Internal error: VP8L predictor run is out of bounds.");
        }

        ref uint origin = ref MemoryMarshal.GetReference(pixels);
        uint left = Unsafe.Add(ref origin, start - 1);
        uint topLeft = Unsafe.Add(ref origin, start - width - 1);
        uint top = Unsafe.Add(ref origin, start - width);

        for (int i = 0; i < runLength; i++)
        {
            ref uint current = ref Unsafe.Add(ref origin, start + i);
            uint topRight = Unsafe.Add(ref origin, start + i - width + 1);

            left = Vp8LPixelMath.AddWrapping(current, TPredictor.Predict(left, top, topLeft, topRight));
            current = left;

            topLeft = top;
            top = topRight;
        }
    }

    /// <summary>
    /// Predictor 11 (the mode photographic lossless images use almost exclusively) with the whole recurrence
    /// kept in vector registers. The output pixel feeds the next pixel's prediction, so the loop is a
    /// latency chain, and the scalar form paid for it with GPR/XMM transfers and a SWAR add on every step.
    /// Here the previous output never leaves its XMM register: <c>psadbw</c> measures both candidates'
    /// distances to the top-left pixel at once, a compare mask picks left or top, and <c>paddb</c> adds the
    /// residual. The same arrangement as libwebp's <c>PredictorAdd11_SSE2</c>; results are bit-identical to
    /// <see cref="SelectScalar"/>.
    /// </summary>
    private static void RunSelectSse2(Span<uint> pixels, int width, int rowBase, int xStart, int runLength)
    {
        int start = rowBase + xStart;
        if (xStart < 1 || runLength < 1 || rowBase < width || start + runLength > pixels.Length || xStart + runLength > width)
        {
            throw new WebpDecodingException("Internal error: VP8L predictor run is out of bounds.");
        }

        ref uint origin = ref MemoryMarshal.GetReference(pixels);
        var left = Vector128.CreateScalar(Unsafe.Add(ref origin, start - 1));

        for (int i = 0; i < runLength; i++)
        {
            ref uint current = ref Unsafe.Add(ref origin, start + i);
            var top = Vector128.CreateScalar(Unsafe.Add(ref origin, start + i - width));
            var topLeft = Vector128.CreateScalar(Unsafe.Add(ref origin, start + i - width - 1));

            // [top, left] and [topLeft, topLeft] as 64-bit lanes (the upper 32 bits of each are zero), so
            // psadbw yields |top - topLeft| in 32-bit lane 0 and |left - topLeft| in lane 2.
            var operands = Sse2.UnpackLow(top.AsUInt64(), left.AsUInt64()).AsByte();
            var reference = Sse2.UnpackLow(topLeft.AsUInt64(), topLeft.AsUInt64()).AsByte();
            var sums = Sse2.SumAbsoluteDifferences(operands, reference).AsInt32();

            var distanceToTop = Sse2.Shuffle(sums, 0x00);
            var distanceToLeft = Sse2.Shuffle(sums, 0xAA);

            // Select returns top unless left is strictly farther from the top-left pixel.
            var pickLeft = Sse2.CompareGreaterThan(distanceToLeft, distanceToTop).AsUInt32();
            var prediction = Vector128.ConditionalSelect(pickLeft, left, top);

            left = Sse2.Add(prediction.AsByte(), Vector128.CreateScalar(current).AsByte()).AsUInt32();
            current = left.ToScalar();
        }
    }

    /// <summary>
    /// One of VP8L's predictor modes, as a static-abstract member so a <see langword="struct"/> implementation
    /// can be passed as a type argument to <see cref="Run{TPredictor}"/> and inlined into it.
    /// </summary>
    private interface IPredictorMode
    {
        /// <summary>Predicts a pixel from its already-reconstructed neighbors.</summary>
        static abstract uint Predict(uint left, uint top, uint topLeft, uint topRight);
    }

    private readonly struct OpaqueBlackPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => 0xFF000000u;
    }

    private readonly struct LeftPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => left;
    }

    private readonly struct TopRightPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => topRight;
    }

    private readonly struct TopLeftPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => topLeft;
    }

    private readonly struct Average3LeftTopTopRightPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average3(left, top, topRight);
    }

    private readonly struct Average2LeftTopLeftPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(left, topLeft);
    }

    private readonly struct Average2LeftTopPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(left, top);
    }

    private readonly struct Average2TopLeftTopPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(topLeft, top);
    }

    private readonly struct Average2TopTopRightPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average2(top, topRight);
    }

    private readonly struct Average4Predictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Average4(left, topLeft, top, topRight);
    }

    private readonly struct SelectPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => Select(top, left, topLeft);
    }

    private readonly struct ClampedAddSubtractFullPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => ClampedAddSubtractFull(left, top, topLeft);
    }

    private readonly struct ClampedAddSubtractHalfPredictor : IPredictorMode
    {
        public static uint Predict(uint left, uint top, uint topLeft, uint topRight) => ClampedAddSubtractHalf(left, top, topLeft);
    }

    /// <summary>
    /// The mode-dispatched reference form of the predictors, kept as the readable statement of what each mode
    /// computes and as the oracle the per-mode specializations above are tested against. Not used by decode.
    /// </summary>
    internal static uint Predict(int mode, ReadOnlySpan<uint> pixels, int index, int width)
    {
        uint left = pixels[index - 1];
        uint top = pixels[index - width];

        switch (mode)
        {
            case 0: return 0xFF000000u;
            case 1: return left;
            case 2: return top;
        }

        uint topLeft = pixels[index - width - 1];
        uint topRight = pixels[index - width + 1];

        return mode switch
        {
            3 => topRight,
            4 => topLeft,
            5 => Average3(left, top, topRight),
            6 => Average2(left, topLeft),
            7 => Average2(left, top),
            8 => Average2(topLeft, top),
            9 => Average2(top, topRight),
            10 => Average4(left, topLeft, top, topRight),
            11 => Select(top, left, topLeft),
            12 => ClampedAddSubtractFull(left, top, topLeft),
            13 => ClampedAddSubtractHalf(left, top, topLeft),

            // Modes 14/15 are not assigned any meaning by the spec; a corrupt/hostile tile image could still
            // produce them (the field is 4 raw bits). Fall back to mode 0 rather than throw mid-pixel-loop,
            // consistent with this decoder's "stop early/degrade gracefully on malformed data" convention.
            _ => 0xFF000000u,
        };
    }

    private static uint Average2(uint a0, uint a1) => (((a0 ^ a1) & 0xFEFEFEFEu) >> 1) + (a0 & a1);

    private static uint Average3(uint a0, uint a1, uint a2) => Average2(Average2(a0, a2), a1);

    private static uint Average4(uint a0, uint a1, uint a2, uint a3) => Average2(Average2(a0, a1), Average2(a2, a3));

    /// <summary>
    /// Picks whichever of <paramref name="a"/> and <paramref name="b"/> is closer to <paramref name="c"/>,
    /// measured as the sum of per-channel absolute differences (libwebp's <c>Select</c>).
    /// </summary>
    /// <remarks>
    /// This is the entire cost of predictor mode 11, which the profile showed is the <em>only</em> mode used
    /// across the whole photographic lossless benchmark asset — and the predictor inverse is ~37% of that
    /// decode. Two sums of four absolute byte differences is a natural byte-lane vector operation, and doing
    /// it that way replaces twelve shift/mask extractions and eight <see cref="Math.Abs(int)"/> calls with one
    /// max, one min and one subtract. Both operand pairs are packed into a single vector — <c>b</c> and
    /// <c>a</c> in the low two 32-bit lanes against <c>c</c> duplicated — so one comparison covers both sums.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Select(uint a, uint b, uint c)
    {
        if (Sse2.IsSupported)
        {
            // psadbw is a horizontal sum of eight absolute byte differences in one instruction (what libwebp's
            // SSE2 predictor 11 uses): lane 0 gets |a - c| summed over the four channels, lane 1 gets |b - c|.
            // This sits on the loop-carried dependency chain (b is the previous output pixel), so the shorter
            // latency is the point -- it replaces a max/min/sub/extract/fold sequence.
            var sadOperands = Vector128.Create((ulong)a, (ulong)b).AsByte();
            var sadReference = Vector128.Create((ulong)c, (ulong)c).AsByte();
            var sums = Sse2.SumAbsoluteDifferences(sadOperands, sadReference).AsUInt64();
            return sums.GetElement(1) <= sums.ToScalar() ? a : b;
        }

        if (!Vector128.IsHardwareAccelerated)
        {
            return SelectScalar(a, b, c);
        }

        var operands = Vector128.Create(b, a, 0u, 0u).AsByte();
        var reference = Vector128.Create(c, c, 0u, 0u).AsByte();
        ulong differences = (Vector128.Max(operands, reference) - Vector128.Min(operands, reference))
            .AsUInt64()
            .ToScalar();

        // Fold the eight |difference| bytes into four 16-bit slots -- no slot can overflow, since each holds
        // at most 255 + 255 -- then add the pairs: slots 0/1 are |b-c|'s four channels, slots 2/3 are |a-c|'s.
        const ulong ByteLanes = 0x00FF00FF00FF00FFu;
        ulong pairSums = (differences & ByteLanes) + ((differences >> 8) & ByteLanes);

        uint distanceToB = (uint)((pairSums & 0xFFFF) + ((pairSums >> 16) & 0xFFFF));
        uint distanceToA = (uint)(((pairSums >> 32) & 0xFFFF) + ((pairSums >> 48) & 0xFFFF));

        return distanceToB <= distanceToA ? a : b;
    }

    /// <summary>Fallback for hardware with no usable vector width, and the reference this file's tests pin <see cref="Select"/> against.</summary>
    internal static uint SelectScalar(uint a, uint b, uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
            Sub3((int)((a >> 16) & 0xFF), (int)((b >> 16) & 0xFF), (int)((c >> 16) & 0xFF)) +
            Sub3((int)((a >> 8) & 0xFF), (int)((b >> 8) & 0xFF), (int)((c >> 8) & 0xFF)) +
            Sub3((int)(a & 0xFF), (int)(b & 0xFF), (int)(c & 0xFF));

        return paMinusPb <= 0 ? a : b;
    }

    private static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        int a = AddSubtractComponentFull((int)(c0 >> 24), (int)(c1 >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentFull((int)((c0 >> 16) & 0xFF), (int)((c1 >> 16) & 0xFF), (int)((c2 >> 16) & 0xFF));
        int g = AddSubtractComponentFull((int)((c0 >> 8) & 0xFF), (int)((c1 >> 8) & 0xFF), (int)((c2 >> 8) & 0xFF));
        int b = AddSubtractComponentFull((int)(c0 & 0xFF), (int)(c1 & 0xFF), (int)(c2 & 0xFF));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int AddSubtractComponentFull(int a, int b, int c) => Clip255(a + b - c);

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        uint average = Average2(c0, c1);
        int a = AddSubtractComponentHalf((int)(average >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentHalf((int)((average >> 16) & 0xFF), (int)((c2 >> 16) & 0xFF));
        int g = AddSubtractComponentHalf((int)((average >> 8) & 0xFF), (int)((c2 >> 8) & 0xFF));
        int b = AddSubtractComponentHalf((int)(average & 0xFF), (int)(c2 & 0xFF));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int AddSubtractComponentHalf(int a, int b) => Clip255(a + ((a - b) / 2));

    private static int Clip255(int value) => Math.Clamp(value, 0, 255);
}
