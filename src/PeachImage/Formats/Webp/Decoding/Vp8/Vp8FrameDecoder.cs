using System.Runtime.InteropServices;
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
    public static IWebpLossyBitstreamDecoder Instance { get; } = new Vp8FrameDecoder();

    public Vp8DecodedFrame Decode(ReadOnlyMemory<byte> vp8Bytes)
    {
        // Vp8BoolDecoder/Vp8Partitions need a real byte[] (a ref struct can't be an array element, so the
        // partitions need a persistent array reference, not a Span) -- but vp8Bytes is already array-backed
        // exactly once, by WebpChunkReader.ReadPayload, so recover that array directly via
        // MemoryMarshal.TryGetArray instead of unconditionally re-copying it with ToArray().
        byte[] data = MemoryMarshal.TryGetArray(vp8Bytes, out ArraySegment<byte> segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : vp8Bytes.ToArray();

        Vp8FrameHeader frameHeader = Vp8FrameHeader.Parse(data);

        var partition0 = new Vp8BoolDecoder(data, frameHeader.HeaderByteLength, frameHeader.FirstPartitionLength);

        partition0.GetFlag(); // color_space - ignored, always treated as standard studio-range YUV.
        partition0.GetFlag(); // clamping_type - ignored, reconstructed pixels are always clamped to [0,255].

        Vp8SegmentHeader segmentHeader = Vp8SegmentHeader.Parse(partition0);
        Vp8LoopFilterHeader loopFilterHeader = Vp8LoopFilterHeader.Parse(partition0);

        int numPartitions = 1 << (int)partition0.GetValue(2);
        int partition0End = frameHeader.HeaderByteLength + frameHeader.FirstPartitionLength;
        Vp8BoolDecoder[] coefficientPartitions = Vp8Partitions.Build(data, partition0End, numPartitions);

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

        try
        {
            InitializeBorders(yPlane, yStride, yPaddedHeight);
            InitializeBorders(uPlane, uvStride, uvPaddedHeight);
            InitializeBorders(vPlane, uvStride, uvPaddedHeight);

            int YOff(int x, int y) => ((y + 1) * yStride) + x + 1;
            int UOff(int x, int y) => ((y + 1) * uvStride) + x + 1;

            var modeDecoder = new Vp8ModeDecoder(mbCols, segmentHeader, useSkipProbability, skipFalseProbability);
            var coeffContext = new Vp8CoefficientContext(mbCols);
            var modes = new Vp8MacroblockModes();

            var segmentAtMb = new int[mbCols * mbRows];
            var isI4x4AtMb = new bool[mbCols * mbRows];
            var innerFilterAtMb = new bool[mbCols * mbRows];

            Span<short> coeffs = stackalloc short[25 * 16];
            Span<short> y2Coeffs = stackalloc short[16];
            Span<short> blockDc = stackalloc short[16];
            Span<byte> aboveRight = stackalloc byte[4];
            Span<bool> colY = stackalloc bool[4];
            Span<bool> colU = stackalloc bool[2];
            Span<bool> colV = stackalloc bool[2];

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

                    if (modes.Skip)
                    {
                        ResetSkippedContext(coeffContext, mbX, modes.IsI4x4);
                        anyNonZero = false;
                    }
                    else
                    {
                        anyNonZero = DecodeMacroblockCoefficients(
                            tokenBr, coeffProbabilities, quant, modes, mbX, coeffContext,
                            coeffs, y2Coeffs, blockDc, colY, colU, colV);
                    }

                    int mbIndex = (mbY * mbCols) + mbX;
                    segmentAtMb[mbIndex] = modes.Segment;
                    isI4x4AtMb[mbIndex] = modes.IsI4x4;

                    // f_inner = is_i4x4 || !skip, where "skip" is the *effective* skip (no residual at all).
                    bool effectiveSkip = !anyNonZero;
                    innerFilterAtMb[mbIndex] = modes.IsI4x4 || !effectiveSkip;

                    int yOrigin = YOff(mbX * 16, mbY * 16);

                    if (!modes.IsI4x4)
                    {
                        Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(modes.YMode, yPlane, yOrigin, yStride, 16, hasAbove, hasLeft);

                        for (int n = 0; n < 16; n++)
                        {
                            int row = n / 4;
                            int col = n % 4;
                            int blockOrigin = YOff((mbX * 16) + (col * 4), (mbY * 16) + (row * 4));
                            Vp8ScalarInverseDct.TransformAndAdd(coeffs.Slice(n * 16, 16), yPlane, blockOrigin, yStride);
                        }
                    }
                    else
                    {
                        ComputeAboveRight(yPlane, yStride, YOff, mbX, mbY, mbCols, hasAbove, aboveRight);

                        for (int n = 0; n < 16; n++)
                        {
                            int row = n / 4;
                            int col = n % 4;
                            int blockOrigin = YOff((mbX * 16) + (col * 4), (mbY * 16) + (row * 4));

                            ReadOnlySpan<byte> ar = col == 3
                                ? aboveRight
                                : yPlane.AsSpan(blockOrigin - yStride + 4, 4);

                            Vp8IntraPrediction4x4.Predict(modes.SubModes[n], yPlane, blockOrigin, yStride, ar);
                            Vp8ScalarInverseDct.TransformAndAdd(coeffs.Slice(n * 16, 16), yPlane, blockOrigin, yStride);
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
                        Vp8ScalarInverseDct.TransformAndAdd(coeffs.Slice((16 + c) * 16, 16), uPlane, uBlockOrigin, uvStride);
                        Vp8ScalarInverseDct.TransformAndAdd(coeffs.Slice((20 + c) * 16, 16), vPlane, uBlockOrigin, uvStride);
                    }
                }
            }

            ApplyLoopFilter(
                yPlane, yStride, uPlane, vPlane, uvStride, mbCols, mbRows,
                loopFilterHeader, filterStrengths, segmentAtMb, isI4x4AtMb, innerFilterAtMb, YOff, UOff);

            return ProduceRgbFrame(yPlane, yStride, uPlane, vPlane, uvStride, mbCols, mbRows, frameHeader.Width, frameHeader.Height, YOff, UOff);
        }
        finally
        {
            WebpBufferPool.Shared.Return(yPlane);
            WebpBufferPool.Shared.Return(uPlane);
            WebpBufferPool.Shared.Return(vPlane);
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

    private static void ResetSkippedContext(Vp8CoefficientContext context, int mbX, bool isI4x4)
    {
        for (int i = 0; i < 4; i++)
        {
            context.AboveY[(mbX * 4) + i] = false;
            context.LeftY[i] = false;
        }

        for (int i = 0; i < 2; i++)
        {
            context.AboveU[(mbX * 2) + i] = false;
            context.LeftU[i] = false;
            context.AboveV[(mbX * 2) + i] = false;
            context.LeftV[i] = false;
        }

        if (!isI4x4)
        {
            context.AboveDc[mbX] = false;
            context.LeftDc = false;
        }
    }

    private static bool DecodeMacroblockCoefficients(
        Vp8BoolDecoder tokenBr,
        byte[] probabilities,
        Vp8QuantMatrix quant,
        Vp8MacroblockModes modes,
        int mbX,
        Vp8CoefficientContext context,
        Span<short> coeffs,
        Span<short> y2Coeffs,
        Span<short> blockDc,
        Span<bool> colY,
        Span<bool> colU,
        Span<bool> colV)
    {
        bool anyNonZero = false;

        if (!modes.IsI4x4)
        {
            y2Coeffs.Clear();
            int dcCtx = (context.LeftDc ? 1 : 0) + (context.AboveDc[mbX] ? 1 : 0);
            int dcLast = Vp8CoefficientDecoder.DecodeBlock(tokenBr, probabilities, 1, dcCtx, 0, quant.Y2Dc, quant.Y2Ac, y2Coeffs);
            bool dcNonZero = dcLast > 0;
            context.LeftDc = dcNonZero;
            context.AboveDc[mbX] = dcNonZero;
            if (dcNonZero)
            {
                anyNonZero = true;
            }

            Vp8ScalarInverseWht.Transform(y2Coeffs, blockDc);
            for (int i = 0; i < 16; i++)
            {
                coeffs[(i * 16) + 0] = blockDc[i];
            }
        }

        int yPlaneType = modes.IsI4x4 ? 3 : 0;
        int yFirst = modes.IsI4x4 ? 0 : 1;

        for (int i = 0; i < 4; i++)
        {
            colY[i] = context.AboveY[(mbX * 4) + i];
        }

        for (int row = 0; row < 4; row++)
        {
            bool rowLeft = context.LeftY[row];
            for (int col = 0; col < 4; col++)
            {
                int idx = (row * 4) + col;
                int ctx = (rowLeft ? 1 : 0) + (colY[col] ? 1 : 0);
                Span<short> block = coeffs.Slice(idx * 16, 16);
                int last = Vp8CoefficientDecoder.DecodeBlock(tokenBr, probabilities, yPlaneType, ctx, yFirst, quant.Y1Dc, quant.Y1Ac, block);
                bool nz = last > yFirst;
                if (nz || block[0] != 0)
                {
                    anyNonZero = true;
                }

                rowLeft = nz;
                colY[col] = nz;
            }

            context.LeftY[row] = rowLeft;
        }

        for (int i = 0; i < 4; i++)
        {
            context.AboveY[(mbX * 4) + i] = colY[i];
        }

        anyNonZero |= DecodeChromaPlane(tokenBr, probabilities, quant, mbX, context.AboveU, context.LeftU, coeffs, colU, blockOffset: 16);
        anyNonZero |= DecodeChromaPlane(tokenBr, probabilities, quant, mbX, context.AboveV, context.LeftV, coeffs, colV, blockOffset: 20);

        return anyNonZero;
    }

    private static bool DecodeChromaPlane(
        Vp8BoolDecoder tokenBr,
        byte[] probabilities,
        Vp8QuantMatrix quant,
        int mbX,
        bool[] above,
        bool[] left,
        Span<short> coeffs,
        Span<bool> col,
        int blockOffset)
    {
        bool anyNonZero = false;

        col[0] = above[(mbX * 2) + 0];
        col[1] = above[(mbX * 2) + 1];

        for (int row = 0; row < 2; row++)
        {
            bool rowLeft = left[row];
            for (int c = 0; c < 2; c++)
            {
                int idx = blockOffset + (row * 2) + c;
                int ctx = (rowLeft ? 1 : 0) + (col[c] ? 1 : 0);
                Span<short> block = coeffs.Slice(idx * 16, 16);
                int last = Vp8CoefficientDecoder.DecodeBlock(tokenBr, probabilities, 2, ctx, 0, quant.UvDc, quant.UvAc, block);
                bool nz = last > 0;
                if (nz || block[0] != 0)
                {
                    anyNonZero = true;
                }

                rowLeft = nz;
                col[c] = nz;
            }

            left[row] = rowLeft;
        }

        above[(mbX * 2) + 0] = col[0];
        above[(mbX * 2) + 1] = col[1];

        return anyNonZero;
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

    private static void ApplyLoopFilter(
        byte[] yPlane,
        int yStride,
        byte[] uPlane,
        byte[] vPlane,
        int uvStride,
        int mbCols,
        int mbRows,
        Vp8LoopFilterHeader header,
        Vp8FilterStrength[,] filterStrengths,
        int[] segmentAtMb,
        bool[] isI4x4AtMb,
        bool[] innerFilterAtMb,
        Func<int, int, int> yOff,
        Func<int, int, int> uOff)
    {
        if (!header.IsFilteringEnabled)
        {
            return;
        }

        for (int mbY = 0; mbY < mbRows; mbY++)
        {
            for (int mbX = 0; mbX < mbCols; mbX++)
            {
                int mbIndex = (mbY * mbCols) + mbX;
                Vp8FilterStrength strength = filterStrengths[segmentAtMb[mbIndex], isI4x4AtMb[mbIndex] ? 1 : 0];
                if (!strength.IsFilteringEnabled)
                {
                    continue;
                }

                bool inner = innerFilterAtMb[mbIndex];
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

    private static Vp8DecodedFrame ProduceRgbFrame(
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
        Func<int, int, int> uOff)
    {
        IVp8ColorConverter converter = Vp8ColorConverterSelector.Select();
        byte[] rgb = new byte[width * height * 3];

        int chromaWidth = mbCols * 8;
        int chromaHeight = mbRows * 8;
        int uBase = uOff(0, 0);

        for (int y = 0; y < height; y++)
        {
            int yRowOff = yOff(0, y);
            int outRowOff = y * width * 3;

            for (int x = 0; x < width; x++)
            {
                byte yy = yPlane[yRowOff + x];
                byte uu = Vp8ChromaUpsampler.Sample(uPlane.AsSpan(uBase), uvStride, chromaWidth, chromaHeight, x, y);
                byte vv = Vp8ChromaUpsampler.Sample(vPlane.AsSpan(uBase), uvStride, chromaWidth, chromaHeight, x, y);

                converter.Convert(yy, uu, vv, rgb.AsSpan(outRowOff + (x * 3), 3));
            }
        }

        return new Vp8DecodedFrame
        {
            Width = width,
            Height = height,
            Rgb24Pixels = rgb,
        };
    }
}
