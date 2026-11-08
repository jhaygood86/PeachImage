using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;
using PeachImage.Formats.Webp.Decoding.Vp8.Dct;
using PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;
using PeachImage.Formats.Webp.Decoding.Vp8.Upsampling;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// Top-level orchestration for the VP8 (lossy) keyframe bitstream decoder: boolean arithmetic decoder,
/// frame/segment/loop-filter headers, per-macroblock intra prediction + DCT coefficient decode, inverse
/// transforms, in-loop deblocking filter, and YUV420-&gt;RGB conversion.
/// </summary>
/// <remarks>
/// Decodes in three full-frame passes: (1) a fused per-macroblock-row pass that decodes modes, decodes and
/// dequantizes coefficients, runs the inverse transforms, and intra-predicts+reconstructs pixels macroblock by
/// macroblock (VP8's causal dependency on already-reconstructed neighbor pixels makes this the natural shape,
/// unlike Jpeg's cleaner decode/reconstruct split); (2) a full-frame in-loop deblocking filter pass in raster
/// order (which reproduces the reference decoder's per-row filter-then-emit ordering exactly, because filtering
/// macroblock (x,y) only ever reads already-filtered neighbors above/left of it); (3) chroma upsampling fused
/// with YUV-&gt;RGB conversion into the final cropped output buffer.
/// </remarks>
internal sealed class Vp8FrameDecoder : IWebpLossyBitstreamDecoder
{
    private const int MbSegmentMask = 3;
    private const int MbIsI4x4 = 4;
    private const int MbInnerFilter = 8;

    public static Vp8FrameDecoder Instance { get; } = new Vp8FrameDecoder();

    public Vp8DecodedFrame Decode(ReadOnlyMemory<byte> vp8Bytes, byte[]? alphaPlane) => DecodeInto(vp8Bytes, alphaPlane, null);

    /// <summary>
    /// Decodes like <see cref="Decode(ReadOnlyMemory{byte}, byte[])"/>; with <paramref name="output"/> the finished rows go straight into
    /// that memory (RGB, or RGBA when <paramref name="alphaPlane"/> is given) and the returned frame carries no pixels of its own.
    /// </summary>
    public Vp8DecodedFrame DecodeInto(ReadOnlyMemory<byte> vp8Bytes, byte[]? alphaPlane, WebpOutput? output)
    {
        // Vp8BoolDecoder/Vp8Partitions need a real byte[] (a ref struct can't be an array element, so the
        // partitions need a persistent array reference, not a Span) -- but vp8Bytes is already array-backed
        // by the container reader, so recover that array directly via MemoryMarshal.TryGetArray instead of
        // copying it. The array may be longer than the chunk (it can come from a pool): dataLength is the chunk.
        byte[] data;
        int dataLength;
        if (MemoryMarshal.TryGetArray(vp8Bytes, out ArraySegment<byte> segment) && segment.Offset == 0)
        {
            data = segment.Array!;
            dataLength = segment.Count;
        }
        else
        {
            data = vp8Bytes.ToArray();
            dataLength = data.Length;
        }

        Vp8FrameHeader frameHeader = Vp8FrameHeader.Parse(data.AsSpan(0, dataLength));

        var partition0 = new Vp8BoolDecoder(data, frameHeader.HeaderByteLength, frameHeader.FirstPartitionLength);

        partition0.GetFlag(); // color_space - ignored, always treated as standard studio-range YUV.
        partition0.GetFlag(); // clamping_type - ignored, reconstructed pixels are always clamped to [0,255].

        Vp8SegmentHeader segmentHeader = Vp8SegmentHeader.Parse(partition0);
        Vp8LoopFilterHeader loopFilterHeader = Vp8LoopFilterHeader.Parse(partition0);

        int numPartitions = 1 << (int)partition0.GetValue(2);
        int partition0End = frameHeader.HeaderByteLength + frameHeader.FirstPartitionLength;
        Vp8BoolDecoder[] coefficientPartitions = Vp8Partitions.Build(data, dataLength, partition0End, numPartitions);

        Vp8QuantIndices quantIndices = Vp8QuantIndices.Parse(partition0);
        Vp8QuantMatrix[] quantMatrices = Vp8Dequantizer.Resolve(quantIndices, segmentHeader);

        partition0.GetFlag(); // refresh_entropy_probs - irrelevant, a WebP `VP8 ` chunk is always a single lone keyframe.
        byte[] coeffProbabilities = Vp8CoefficientDecoder.ParseProbabilityUpdates(partition0);
        bool useSkipProbability = partition0.GetFlag();
        int skipFalseProbability = useSkipProbability ? (int)partition0.GetValue(8) : 0;

        var filterStrengths = Vp8LoopFilterLevelResolver.Resolve(loopFilterHeader, segmentHeader);

        int mbCols = (frameHeader.Width + 15) / 16;
        int mbRows = (frameHeader.Height + 15) / 16;

        int yStride = (mbCols * 16) + 1;
        int yPaddedHeight = (mbRows * 16) + 1;
        int uvStride = (mbCols * 8) + 1;
        int uvPaddedHeight = (mbRows * 8) + 1;

        byte[] yPlane = WebpBufferPool.Shared.Rent(yStride * yPaddedHeight);
        byte[] uPlane = WebpBufferPool.Shared.Rent(uvStride * uvPaddedHeight);
        byte[] vPlane = WebpBufferPool.Shared.Rent(uvStride * uvPaddedHeight);

        // What the loop filter needs to know about each macroblock, one byte each: segment in the low two bits, then the
        // 4x4-mode and inner-edge flags. Every entry is written before the filter reads it.
        byte[] mbInfo = WebpBufferPool.Shared.Rent(mbCols * mbRows);

        try
        {
            InitializeBorders(yPlane, yStride, yPaddedHeight);
            InitializeBorders(uPlane, uvStride, uvPaddedHeight);
            InitializeBorders(vPlane, uvStride, uvPaddedHeight);

            int YOff(int x, int y) => ((y + 1) * yStride) + x + 1;
            int UOff(int x, int y) => ((y + 1) * uvStride) + x + 1;

            // Passing a local function where a delegate is expected builds a new delegate each time, so the ones the per-macroblock and
            // per-row calls take are made once.
            Func<int, int, int> yOffset = YOff;
            Func<int, int, int> uOffset = UOff;

            var modeDecoder = new Vp8ModeDecoder(mbCols, segmentHeader, useSkipProbability, skipFalseProbability);
            var coeffContext = new Vp8TokenContext(mbCols);
            var modes = new Vp8MacroblockModes();


            Span<short> coeffs = stackalloc short[25 * 16];
            Span<byte> aboveRight = stackalloc byte[4];

            using var writer = new RgbRowWriter(
                yPlane, yStride, uPlane, vPlane, uvStride, mbCols, mbRows, frameHeader.Width, frameHeader.Height, yOffset, uOffset, alphaPlane, output);

            for (int mbY = 0; mbY < mbRows; mbY++)
            {
                modeDecoder.StartRow();
                coeffContext.StartRow();
                Vp8BoolDecoder tokenBr = coefficientPartitions[mbY % numPartitions];
                bool hasAbove = mbY > 0;

                for (int mbX = 0; mbX < mbCols; mbX++)
                {
                    modeDecoder.DecodeMacroblock(partition0, mbX, modes);
                    bool hasLeft = mbX > 0;

                    Vp8QuantMatrix quant = quantMatrices[modes.Segment];

                    coeffs.Clear();
                    bool anyNonZero;

                    uint nonZeroBlocks;
                    uint acBlocks;

                    if (modes.Skip)
                    {
                        coeffContext.ResetSkipped(mbX, modes.IsI4x4);
                        anyNonZero = false;
                        nonZeroBlocks = 0;
                        acBlocks = 0;
                    }
                    else
                    {
                        Vp8CoefficientDecoder.DecodeMacroblock(
                            tokenBr, coeffProbabilities, quant, modes.IsI4x4, ref coeffContext.Above[mbX], ref coeffContext.Left,
                            coeffs, out nonZeroBlocks, out acBlocks, out anyNonZero);
                    }

                    int mbIndex = (mbY * mbCols) + mbX;

                    // f_inner = is_i4x4 || !skip, where "skip" is the *effective* skip (no residual at all).
                    bool effectiveSkip = !anyNonZero;
                    mbInfo[mbIndex] = (byte)(modes.Segment | (modes.IsI4x4 ? MbIsI4x4 : 0) | (modes.IsI4x4 || !effectiveSkip ? MbInnerFilter : 0));

                    int yOrigin = YOff(mbX * 16, mbY * 16);

                    if (!modes.IsI4x4)
                    {
                        Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(modes.YMode, yPlane, yOrigin, yStride, 16, hasAbove, hasLeft);

                        for (int n = 0; n < 16; n++)
                        {
                            if ((nonZeroBlocks & (1u << n)) == 0)
                            {
                                continue;
                            }

                            int row = n / 4;
                            int col = n % 4;
                            int blockOrigin = YOff((mbX * 16) + (col * 4), (mbY * 16) + (row * 4));
                            AddResidual(coeffs, n, acBlocks, yPlane, blockOrigin, yStride);
                        }
                    }
                    else
                    {
                        ComputeAboveRight(yPlane, yStride, yOffset, mbX, mbY, mbCols, hasAbove, aboveRight);

                        for (int n = 0; n < 16; n++)
                        {
                            int row = n / 4;
                            int col = n % 4;
                            int blockOrigin = YOff((mbX * 16) + (col * 4), (mbY * 16) + (row * 4));

                            ReadOnlySpan<byte> ar = col == 3
                                ? aboveRight
                                : yPlane.AsSpan(blockOrigin - yStride + 4, 4);

                            // Prediction runs for every subblock regardless — each one's reconstructed pixels
                            // are what the next subblock predicts from. Only the residual can be skipped.
                            Vp8IntraPrediction4x4.Predict(modes.SubModes[n], yPlane, blockOrigin, yStride, ar);

                            if ((nonZeroBlocks & (1u << n)) != 0)
                            {
                                AddResidual(coeffs, n, acBlocks, yPlane, blockOrigin, yStride);
                            }
                        }
                    }

                    int uOrigin = UOff(mbX * 8, mbY * 8);
                    Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(modes.UvMode, uPlane, uOrigin, uvStride, 8, hasAbove, hasLeft);
                    Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(modes.UvMode, vPlane, uOrigin, uvStride, 8, hasAbove, hasLeft);

                    for (int c = 0; c < 4; c++)
                    {
                        int row = c / 2;
                        int col = c % 2;
                        int uBlockOrigin = UOff((mbX * 8) + (col * 4), (mbY * 8) + (row * 4));

                        if ((nonZeroBlocks & (1u << (16 + c))) != 0)
                        {
                            AddResidual(coeffs, 16 + c, acBlocks, uPlane, uBlockOrigin, uvStride);
                        }

                        if ((nonZeroBlocks & (1u << (20 + c))) != 0)
                        {
                            AddResidual(coeffs, 20 + c, acBlocks, vPlane, uBlockOrigin, uvStride);
                        }
                    }
                }

                // Intra prediction reads the unfiltered row above, so row mbY-1 is filtered only now that row mbY is
                // reconstructed. That row's filtering also finishes row mbY-2, and lines of the row itself down to
                // line 12 (the next row's top edge still reaches up to three lines into it); chroma is final through line 4
                // of its 8, and the upsampler looks one chroma row past the line it converts, so lines below
                // (mbY-1)*16 + 8 wait.
                if (mbY > 0)
                {
                    ApplyLoopFilterRow(
                        mbY - 1, yPlane, yStride, uPlane, vPlane, uvStride, mbCols,
                        loopFilterHeader, filterStrengths, mbInfo, yOffset, uOffset);
                    writer.ConvertTo(Math.Min(((mbY - 1) * 16) + 8, frameHeader.Height));
                }
            }

            // The last row has nothing after it to wait for.
            ApplyLoopFilterRow(
                mbRows - 1, yPlane, yStride, uPlane, vPlane, uvStride, mbCols,
                loopFilterHeader, filterStrengths, mbInfo, yOffset, uOffset);
            writer.ConvertTo(frameHeader.Height);
            return writer.Finish();
        }
        finally
        {
            WebpBufferPool.Shared.Return(yPlane);
            WebpBufferPool.Shared.Return(uPlane);
            WebpBufferPool.Shared.Return(vPlane);
            WebpBufferPool.Shared.Return(mbInfo);
        }
    }

    /// <summary>
    /// Pre-fills the plane's virtual border: an entire row above the real data (index -1, mapped to row 0 of
    /// the padded buffer) set to 127, and an entire column to the left of the real data (index -1, mapped to
    /// column 0) set to 129, for every real row. This reproduces libwebp's <c>ReconstructRow</c> border setup
    /// (RFC 6386 section 12.2's "unavailable" edge values) uniformly: the frame's real top row always has no
    /// "above" neighbor (127, including its own corner), and the frame's real left column always has no "left"
    /// neighbor (129, including the corner when a real row above exists) - both without any conditional logic
    /// in the prediction routines themselves.
    /// </summary>
    private static void InitializeBorders(byte[] plane, int stride, int paddedHeight)
    {
        plane.AsSpan(0, stride).Fill(127);
        for (int row = 1; row < paddedHeight; row++)
        {
            plane[row * stride] = 129;
        }
    }

    /// <summary>
    /// Adds block <paramref name="blockIndex"/>'s residual, taking the DC-only shortcut when
    /// <paramref name="acBlocks"/> says the block has no AC coefficients. Callers have already established
    /// from <c>nonZeroBlocks</c> that the block has something to add.
    /// </summary>
    private static void AddResidual(Span<short> coeffs, int blockIndex, uint acBlocks, byte[] plane, int origin, int stride)
    {
        // One range check covers the block's sixteen coefficients and its four rows of four pixels; the kernels below then work on
        // raw references.
        if ((uint)blockIndex >= 25 || (blockIndex + 1) * 16 > coeffs.Length
            || origin < 0 || stride < 0 || (long)origin + (3L * stride) + 4 > plane.Length)
        {
            throw new ArgumentException("The block lies outside its buffers.");
        }

        ref short block = ref Unsafe.Add(ref MemoryMarshal.GetReference(coeffs), blockIndex * 16);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), origin);

        if ((acBlocks & (1u << blockIndex)) != 0)
        {
            // Vp8VectorInverseDct.CanTransform checks Sse2/AdvSimd.Arm64.IsSupported, which RyuJIT treats as a JIT-time
            // intrinsic constant -- this branch is resolved once per process, not re-evaluated per block.
            if (Vp8VectorInverseDct.CanTransform)
            {
                Vp8VectorInverseDct.TransformFullAndAdd(ref block, ref dst, stride);
            }
            else
            {
                Vp8ScalarInverseDct.TransformFullAndAdd(coeffs.Slice(blockIndex * 16, 16), plane, origin, stride);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vp8VectorInverseDct.AddDcOnly(ref block, ref dst, stride);
        }
        else
        {
            Vp8ScalarInverseDct.TransformDcOnlyAndAdd(coeffs.Slice(blockIndex * 16, 16), plane, origin, stride);
        }
    }

    /// <summary>
    /// Computes the 4 "above-right" pixels shared by every subblock in the rightmost column (x=3) of a B_PRED
    /// macroblock's 4x4 grid, reproducing libwebp's <c>top_right[BPS] = top_right[2*BPS] = top_right[3*BPS] =
    /// top_right[0]</c> replication quirk: subblocks below the top row of the macroblock don't have a real
    /// "above-right" neighbor (it would belong to a not-yet-decoded macroblock), so the same 4 pixels from the
    /// row directly above the macroblock are reused for every row.
    /// </summary>
    private static void ComputeAboveRight(byte[] yPlane, int yStride, Func<int, int, int> yOff, int mbX, int mbY, int mbCols, bool hasAbove, Span<byte> aboveRight)
    {
        if (!hasAbove)
        {
            aboveRight.Fill(127);
            return;
        }

        if (mbX == mbCols - 1)
        {
            byte value = yPlane[yOff((mbX * 16) + 15, (mbY * 16) - 1)];
            aboveRight.Fill(value);
            return;
        }

        int baseOffset = yOff((mbX * 16) + 16, (mbY * 16) - 1);
        for (int k = 0; k < 4; k++)
        {
            aboveRight[k] = yPlane[baseOffset + k];
        }
    }

    /// <summary>Loop-filters macroblock row <paramref name="mbY"/>. Rows are filtered in raster order, so calling this for each row in turn is the same as one pass over the frame.</summary>
    private static void ApplyLoopFilterRow(
        int mbY,
        byte[] yPlane,
        int yStride,
        byte[] uPlane,
        byte[] vPlane,
        int uvStride,
        int mbCols,
        Vp8LoopFilterHeader header,
        Vp8FilterStrength[,] filterStrengths,
        byte[] mbInfo,
        Func<int, int, int> yOff,
        Func<int, int, int> uOff)
    {
        if (!header.IsFilteringEnabled)
        {
            return;
        }

        {
            for (int mbX = 0; mbX < mbCols; mbX++)
            {
                int mbIndex = (mbY * mbCols) + mbX;
                byte info = mbInfo[mbIndex];
                Vp8FilterStrength strength = filterStrengths[info & MbSegmentMask, (info & MbIsI4x4) != 0 ? 1 : 0];
                if (!strength.IsFilteringEnabled)
                {
                    continue;
                }

                bool inner = (info & MbInnerFilter) != 0;
                int yOrigin = yOff(mbX * 16, mbY * 16);

                if (header.Simple)
                {
                    if (mbX > 0)
                    {
                        Vp8SimpleLoopFilter.FilterLeftEdge(yPlane, yOrigin, yStride, strength.Limit + 4);
                    }

                    if (inner)
                    {
                        Vp8SimpleLoopFilter.FilterLeftEdgesInner(yPlane, yOrigin, yStride, strength.Limit);
                    }

                    if (mbY > 0)
                    {
                        Vp8SimpleLoopFilter.FilterTopEdge(yPlane, yOrigin, yStride, strength.Limit + 4);
                    }

                    if (inner)
                    {
                        Vp8SimpleLoopFilter.FilterTopEdgesInner(yPlane, yOrigin, yStride, strength.Limit);
                    }
                }
                else if (Vp8EdgeFilter.IsSupported)
                {
                    int uOrigin = uOff(mbX * 8, mbY * 8);

                    // One extent check for everything this macroblock's edges touch, in place of a bounds check per row of each edge.
                    // The planes are padded to whole macroblocks, so this only fails for a plane that is not the decoder's own.
                    int yLowest = yOrigin - (mbY > 0 ? 4 * yStride : 0) - (mbX > 0 ? 4 : 0);
                    int uLowest = uOrigin - (mbY > 0 ? 4 * uvStride : 0) - (mbX > 0 ? 4 : 0);
                    if (yLowest < 0 || uLowest < 0
                        || yOrigin + (15 * yStride) + 16 > yPlane.Length
                        || uOrigin + (7 * uvStride) + 8 > Math.Min(uPlane.Length, vPlane.Length))
                    {
                        throw new InvalidOperationException("The loop filter's macroblock lies outside its plane.");
                    }

                    ref byte yAt = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(yPlane), yOrigin);
                    ref byte uAt = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(uPlane), uOrigin);
                    ref byte vAt = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(vPlane), uOrigin);
                    int edgeLimit = strength.Limit + 4;

                    if (mbX > 0)
                    {
                        Vp8EdgeFilter.LumaVertical(ref yAt, yStride, true, edgeLimit, strength.InteriorLimit, strength.HevThreshold);
                        Vp8EdgeFilter.ChromaVertical(ref uAt, ref vAt, uvStride, true, edgeLimit, strength.InteriorLimit, strength.HevThreshold);
                    }

                    if (inner)
                    {
                        for (int k = 4; k <= 12; k += 4)
                        {
                            Vp8EdgeFilter.LumaVertical(ref Unsafe.Add(ref yAt, k), yStride, false, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                        }

                        Vp8EdgeFilter.ChromaVertical(ref Unsafe.Add(ref uAt, 4), ref Unsafe.Add(ref vAt, 4), uvStride, false, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                    }

                    if (mbY > 0)
                    {
                        Vp8EdgeFilter.LumaHorizontal(ref yAt, yStride, true, edgeLimit, strength.InteriorLimit, strength.HevThreshold);
                        Vp8EdgeFilter.ChromaHorizontal(ref uAt, ref vAt, uvStride, true, edgeLimit, strength.InteriorLimit, strength.HevThreshold);
                    }

                    if (inner)
                    {
                        for (int k = 4; k <= 12; k += 4)
                        {
                            Vp8EdgeFilter.LumaHorizontal(ref Unsafe.Add(ref yAt, k * yStride), yStride, false, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                        }

                        Vp8EdgeFilter.ChromaHorizontal(ref Unsafe.Add(ref uAt, 4 * uvStride), ref Unsafe.Add(ref vAt, 4 * uvStride), uvStride, false, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                    }
                }
                else
                {
                    int uOrigin = uOff(mbX * 8, mbY * 8);

                    if (mbX > 0)
                    {
                        Vp8NormalLoopFilter.FilterLeftEdge16(yPlane, yOrigin, yStride, strength.Limit + 4, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterLeftEdge8(uPlane, uOrigin, uvStride, strength.Limit + 4, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterLeftEdge8(vPlane, uOrigin, uvStride, strength.Limit + 4, strength.InteriorLimit, strength.HevThreshold);
                    }

                    if (inner)
                    {
                        Vp8NormalLoopFilter.FilterLeftEdgesInner16(yPlane, yOrigin, yStride, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterLeftEdgeInner8(uPlane, uOrigin, uvStride, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterLeftEdgeInner8(vPlane, uOrigin, uvStride, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                    }

                    if (mbY > 0)
                    {
                        Vp8NormalLoopFilter.FilterTopEdge16(yPlane, yOrigin, yStride, strength.Limit + 4, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterTopEdge8(uPlane, uOrigin, uvStride, strength.Limit + 4, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterTopEdge8(vPlane, uOrigin, uvStride, strength.Limit + 4, strength.InteriorLimit, strength.HevThreshold);
                    }

                    if (inner)
                    {
                        Vp8NormalLoopFilter.FilterTopEdgesInner16(yPlane, yOrigin, yStride, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterTopEdgeInner8(uPlane, uOrigin, uvStride, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                        Vp8NormalLoopFilter.FilterTopEdgeInner8(vPlane, uOrigin, uvStride, strength.Limit, strength.InteriorLimit, strength.HevThreshold);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Converts finished YUV lines to RGB (or RGBA) as the frame decode makes them final, so the planes are read while still in
    /// cache instead of in a separate pass over the whole frame.
    /// </summary>
    /// <remarks>
    /// When an alpha plane is supplied this writes RGBA32 directly rather than producing an RGB24 buffer for a caller to widen
    /// afterward: each row is converted into a small pool-rented RGB scratch row (not image-sized) and spliced with that
    /// row's alpha byte straight into the final buffer as it is produced. <see cref="IVp8ColorConverter.ConvertRow"/> itself always
    /// writes plain 3-byte-stride RGB, whether that goes straight to the final buffer (no alpha) or through the scratch row.
    /// </remarks>
    private sealed class RgbRowWriter : IDisposable
    {
        private readonly byte[] _yPlane;
        private readonly byte[] _uPlane;
        private readonly byte[] _vPlane;
        private readonly int _yStride;
        private readonly int _uvStride;
        private readonly int _width;
        private readonly int _height;
        private readonly int _chromaWidth;
        private readonly int _chromaHeight;
        private readonly int _uBase;
        private readonly int _bytesPerPixel;
        private readonly int _rowBytes;
        private readonly Func<int, int, int> _yOff;
        private readonly byte[]? _alphaPlane;
        private readonly WebpOutput? _target;
        private readonly IVp8ColorConverter _converter;
        private readonly byte[] _output;
        private readonly byte[] _uRow;
        private readonly byte[] _vRow;
        private readonly byte[]? _rgbRow;
        private int _nextRow;

        public RgbRowWriter(
            byte[] yPlane,
            int yStride,
            byte[] uPlane,
            byte[] vPlane,
            int uvStride,
            int mbCols,
            int mbRows,
            int width,
            int height,
            Func<int, int, int> yOff,
            Func<int, int, int> uOff,
            byte[]? alphaPlane,
            WebpOutput? target)
        {
            _yPlane = yPlane;
            _uPlane = uPlane;
            _vPlane = vPlane;
            _yStride = yStride;
            _uvStride = uvStride;
            _width = width;
            _height = height;
            _yOff = yOff;
            _alphaPlane = alphaPlane;
            _target = target;
            _converter = Vp8ColorConverterSelector.Instance;
            _bytesPerPixel = alphaPlane is null ? 3 : 4;
            _rowBytes = width * _bytesPerPixel;
            _chromaWidth = mbCols * 8;
            _chromaHeight = mbRows * 8;
            _uBase = uOff(0, 0);

            // With a caller-provided destination there is no array at all: rows are written where they belong. Otherwise it is
            // exactly width * height * bytesPerPixel bytes (callers, including tests, rely on Pixels.Length being exact), so a
            // plain heap allocation, uninitialized because every byte of it is written.
            _output = target is null
                ? GC.AllocateUninitializedArray<byte>(checked((int)((long)width * height * _bytesPerPixel)))
                : [];

            // Two full-width scratch rows for the upsampled chroma, rented rather than allocated: at most 16383 bytes each, so
            // they stay resident in L1 across the whole frame.
            _uRow = WebpBufferPool.Shared.Rent(width);
            _vRow = WebpBufferPool.Shared.Rent(width);

            // Only rented when alpha is present with a converter that cannot splice it itself.
            _rgbRow = alphaPlane is null || _converter is Sse2Vp8ColorConverter ? null : WebpBufferPool.Shared.Rent(width * 3);
        }

        /// <summary>Converts every line below the ones already converted and above <paramref name="endRow"/>.</summary>
        public void ConvertTo(int endRow)
        {
            for (int y = _nextRow; y < endRow; y++)
            {
                // Every output row interpolates between exactly two chroma rows: the one it sits closest to, and its neighbour
                // on the side the output row leans toward (clamped at the plane edges, reproducing libwebp's edge replication).
                // Resolving those once per row is what lets the upsampler and converter run as row kernels.
                int nearRow = y >> 1;
                int farRow = Clamp((y >> 1) + (((y & 1) == 1) ? 1 : -1), _chromaHeight - 1);

                var uNear = _uPlane.AsSpan(_uBase + (nearRow * _uvStride), _chromaWidth);
                var uFar = _uPlane.AsSpan(_uBase + (farRow * _uvStride), _chromaWidth);
                var vNear = _vPlane.AsSpan(_uBase + (nearRow * _uvStride), _chromaWidth);
                var vFar = _vPlane.AsSpan(_uBase + (farRow * _uvStride), _chromaWidth);

                Vp8ChromaUpsampler.UpsampleRow(uNear, uFar, _uRow, _width, _chromaWidth);
                Vp8ChromaUpsampler.UpsampleRow(vNear, vFar, _vRow, _width, _chromaWidth);

                var yRow = _yPlane.AsSpan(_yOff(0, y), _width);
                var outputRow = _target is { } destination ? destination.Row(y, _rowBytes) : _output.AsSpan(y * _rowBytes, _rowBytes);

                if (_alphaPlane is null)
                {
                    _converter.ConvertRow(yRow, _uRow, _vRow, outputRow, _width);
                }
                else if (_converter is Sse2Vp8ColorConverter sse2Converter)
                {
                    sse2Converter.ConvertRowWithAlpha(yRow, _uRow, _vRow, _alphaPlane.AsSpan(y * _width, _width), outputRow, _width);
                }
                else
                {
                    _converter.ConvertRow(yRow, _uRow, _vRow, _rgbRow!.AsSpan(0, _width * 3), _width);
                    SpliceAlphaRow(_rgbRow, _alphaPlane.AsSpan(y * _width, _width), outputRow, _width);
                }
            }

            _nextRow = Math.Max(_nextRow, endRow);
        }

        public Vp8DecodedFrame Finish() => new()
        {
            Width = _width,
            Height = _height,
            PixelFormat = _alphaPlane is null ? PixelFormat.Rgb24 : PixelFormat.Rgba32,
            Pixels = _output,
        };

        public void Dispose()
        {
            WebpBufferPool.Shared.Return(_uRow);
            WebpBufferPool.Shared.Return(_vRow);
            if (_rgbRow is not null)
            {
                WebpBufferPool.Shared.Return(_rgbRow);
            }
        }
    }

    /// <summary>Interleaves one already-converted RGB24 row with one alpha row into an RGBA32 row -- the per-row equivalent of what <c>WebpDecoder.InterleaveRgba</c> used to do over the whole image at once.</summary>
    private static void SpliceAlphaRow(ReadOnlySpan<byte> rgbRow, ReadOnlySpan<byte> alphaRow, Span<byte> rgbaRow, int width)
    {
        for (int x = 0; x < width; x++)
        {
            rgbaRow[(x * 4) + 0] = rgbRow[(x * 3) + 0];
            rgbaRow[(x * 4) + 1] = rgbRow[(x * 3) + 1];
            rgbaRow[(x * 4) + 2] = rgbRow[(x * 3) + 2];
            rgbaRow[(x * 4) + 3] = alphaRow[x];
        }
    }

    private static int Clamp(int value, int max) => value < 0 ? 0 : value > max ? max : value;
}
