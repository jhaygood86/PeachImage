using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Decoding.Vp8.Dct;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// The fused per-macroblock-row encode loop: mode decision, forward transform, quantization, reconstruction
/// (updating the encoder's own working planes so later macroblocks predict from real, already-reconstructed
/// pixels — never from unfiltered-but-not-yet-quantized source data), and entropy coding — the encode-side
/// counterpart of <see cref="Decoding.Vp8.Vp8FrameDecoder.Decode"/>'s main loop, structured the same way for the
/// same reason: VP8 intra prediction's causal dependency on already-reconstructed neighbor pixels makes this
/// fused shape unavoidable, the same way it is on decode.
/// </summary>
/// <remarks>
/// <para>
/// Mode decision is distortion-only (sum of absolute differences, via <see cref="Vp8ModeDecision"/>), and the
/// 16x16-vs-B_PRED choice is a simple SAD-threshold heuristic (see <see cref="BPredSadThreshold"/>) rather than
/// a real two-candidate rate-distortion comparison — evaluating both candidates fully (to compare their actual
/// achieved SAD) would mean either running B_PRED's real causal reconstruction speculatively and discarding it,
/// or building rollback machinery for the working planes; a threshold on the already-computed 16x16 SAD gets a
/// working adaptive choice (smooth regions stay 16x16, detailed ones switch to B_PRED) without either. Both are
/// v1 placeholders to recalibrate once real corpus/PSNR measurements exist, not values derived from theory.
/// </para>
/// <para>
/// Per-frame coefficient probability adaptation and per-segment quantization are both out of scope for the same
/// reason they are in <see cref="Vp8HeaderWriter"/>: real compression wins, not required for a correct v1
/// bitstream.
/// </para>
/// </remarks>
internal static class Vp8FrameEncoder
{
    /// <summary>
    /// v1 placeholder: the whole-16x16-block prediction's SAD (summed over 256 pixels) above which a
    /// macroblock switches to B_PRED. Roughly an average per-pixel absolute residual of 6.
    /// </summary>
    private const int BPredSadThreshold = 16 * 16 * 6;

    public static void Encode(
        byte[] rgb, int width, int height,
        Vp8BoolEncoder partition0, Vp8BoolEncoder coefficientEncoder,
        Vp8QuantMatrix quant, bool useSkipProbability, int skipFalseProbability)
    {
        int mbCols = (width + 15) / 16;
        int mbRows = (height + 15) / 16;

        int yStride = (mbCols * 16) + 1;
        int yPaddedHeight = (mbRows * 16) + 1;
        int uvStride = (mbCols * 8) + 1;
        int uvPaddedHeight = (mbRows * 8) + 1;

        var recY = new byte[yStride * yPaddedHeight];
        var recU = new byte[uvStride * uvPaddedHeight];
        var recV = new byte[uvStride * uvPaddedHeight];
        InitializeBorders(recY, yStride, yPaddedHeight);
        InitializeBorders(recU, uvStride, uvPaddedHeight);
        InitializeBorders(recV, uvStride, uvPaddedHeight);

        int YOff(int x, int y) => ((y + 1) * yStride) + x + 1;
        int UOff(int x, int y) => ((y + 1) * uvStride) + x + 1;

        int srcYStride = mbCols * 16;
        int srcYHeight = mbRows * 16;
        int srcUvStride = mbCols * 8;
        int srcUvHeight = mbRows * 8;

        var srcY = new byte[srcYStride * srcYHeight];
        var srcU = new byte[srcUvStride * srcUvHeight];
        var srcV = new byte[srcUvStride * srcUvHeight];

        Vp8ForwardColorConverter.ConvertPlanes(rgb, width, height, srcY, srcYStride, srcU, srcV, srcUvStride);
        PadPlaneToGrid(srcY, width, height, srcYStride, srcYHeight);
        int chromaRealWidth = (width + 1) / 2;
        int chromaRealHeight = (height + 1) / 2;
        PadPlaneToGrid(srcU, chromaRealWidth, chromaRealHeight, srcUvStride, srcUvHeight);
        PadPlaneToGrid(srcV, chromaRealWidth, chromaRealHeight, srcUvStride, srcUvHeight);

        var modeWriter = new Vp8ModeWriter(mbCols);
        var coeffContext = new Vp8CoefficientContext(mbCols);
        byte[] coeffProbabilities = Vp8CoefficientProbabilities.DefaultFlat;

        Span<byte> aboveRight = stackalloc byte[4];

        for (int mbY = 0; mbY < mbRows; mbY++)
        {
            modeWriter.StartRow();
            coeffContext.StartRow();
            bool hasAbove = mbY > 0;

            for (int mbX = 0; mbX < mbCols; mbX++)
            {
                bool hasLeft = mbX > 0;

                ProcessMacroblock(
                    recY, yStride, recU, recV, uvStride,
                    srcY, srcYStride, srcU, srcV, srcUvStride,
                    YOff, UOff, mbX, mbY, mbCols, hasAbove, hasLeft,
                    quant, coeffContext, coeffProbabilities,
                    modeWriter, partition0, coefficientEncoder,
                    useSkipProbability, skipFalseProbability, aboveRight);
            }
        }
    }

    private static void ProcessMacroblock(
        byte[] recY, int yStride, byte[] recU, byte[] recV, int uvStride,
        byte[] srcY, int srcYStride, byte[] srcU, byte[] srcV, int srcUvStride,
        Func<int, int, int> yOff, Func<int, int, int> uOff,
        int mbX, int mbY, int mbCols, bool hasAbove, bool hasLeft,
        Vp8QuantMatrix quant, Vp8CoefficientContext coeffContext, byte[] coeffProbabilities,
        Vp8ModeWriter modeWriter, Vp8BoolEncoder partition0, Vp8BoolEncoder coefficientEncoder,
        bool useSkipProbability, int skipFalseProbability, Span<byte> aboveRight)
    {
        var quantizedBlocks = new short[24][];
        var lastBlocks = new int[24];
        var y2Quantized = new short[16];
        int y2Last = 0;
        bool anyNonZero = false;

        int yOrigin = yOff(mbX * 16, mbY * 16);
        int srcYOrigin = (mbY * 16 * srcYStride) + (mbX * 16);

        int wholeMode = Vp8ModeDecision.SelectWholeBlockMode(recY, yOrigin, yStride, 16, hasAbove, hasLeft, srcY, srcYOrigin, srcYStride, out int wholeSad);
        bool isI4x4 = wholeSad > BPredSadThreshold;

        int[]? subModes = null;

        if (!isI4x4)
        {
            var dcValues = new short[16];
            var rawCoeffs = new short[16][];

            for (int n = 0; n < 16; n++)
            {
                int row = n / 4;
                int col = n % 4;
                int blockOrigin = yOff((mbX * 16) + (col * 4), (mbY * 16) + (row * 4));
                int srcBlockOrigin = ((mbY * 16 + (row * 4)) * srcYStride) + (mbX * 16) + (col * 4);

                var raw = new short[16];
                Vp8ForwardDct.Transform(srcY, srcBlockOrigin, srcYStride, recY, blockOrigin, yStride, raw);
                rawCoeffs[n] = raw;
                dcValues[n] = raw[0];
            }

            var y2Raw = new short[16];
            Vp8ForwardWht.Transform(dcValues, y2Raw);
            y2Last = Vp8ForwardQuantizer.Quantize(y2Raw, quant.Y2Dc, quant.Y2Ac, y2Quantized);
            if (y2Last > 0)
            {
                anyNonZero = true;
            }

            var y2Dequant = new short[16];
            Vp8ForwardQuantizer.Dequantize(y2Quantized, quant.Y2Dc, quant.Y2Ac, y2Dequant);
            var blockDc = new short[16];
            Vp8ScalarInverseWht.Transform(y2Dequant, blockDc);

            for (int n = 0; n < 16; n++)
            {
                var quantizedAc = new short[16];
                int last = Vp8ForwardQuantizer.Quantize(rawCoeffs[n], quant.Y1Dc, quant.Y1Ac, quantizedAc);
                quantizedBlocks[n] = quantizedAc;
                lastBlocks[n] = last;
                if (last > 1)
                {
                    anyNonZero = true;
                }

                var dequant = new short[16];
                Vp8ForwardQuantizer.Dequantize(quantizedAc, quant.Y1Dc, quant.Y1Ac, dequant);
                dequant[0] = blockDc[n]; // The Y2/WHT-derived DC replaces this block's own (unused) DC token.

                int row = n / 4;
                int col = n % 4;
                int blockOrigin = yOff((mbX * 16) + (col * 4), (mbY * 16) + (row * 4));
                Vp8ScalarInverseDct.TransformAndAdd(dequant, recY, blockOrigin, yStride);
            }
        }
        else
        {
            subModes = new int[16];
            for (int n = 0; n < 16; n++)
            {
                int row = n / 4;
                int col = n % 4;
                int blockOrigin = yOff((mbX * 16) + (col * 4), (mbY * 16) + (row * 4));
                int srcBlockOrigin = ((mbY * 16 + (row * 4)) * srcYStride) + (mbX * 16) + (col * 4);

                ReadOnlySpan<byte> ar = col == 3
                    ? ComputeAboveRight(recY, yOff, mbX, mbY, mbCols, hasAbove, aboveRight)
                    : recY.AsSpan(blockOrigin - yStride + 4, 4);

                int mode = Vp8ModeDecision.SelectSubblockMode(recY, blockOrigin, yStride, ar, srcY, srcBlockOrigin, srcYStride, out _);
                subModes[n] = mode;

                var raw = new short[16];
                Vp8ForwardDct.Transform(srcY, srcBlockOrigin, srcYStride, recY, blockOrigin, yStride, raw);

                var quantizedAc = new short[16];
                int last = Vp8ForwardQuantizer.Quantize(raw, quant.Y1Dc, quant.Y1Ac, quantizedAc);
                quantizedBlocks[n] = quantizedAc;
                lastBlocks[n] = last;
                if (last > 0)
                {
                    anyNonZero = true;
                }

                var dequant = new short[16];
                Vp8ForwardQuantizer.Dequantize(quantizedAc, quant.Y1Dc, quant.Y1Ac, dequant);
                Vp8ScalarInverseDct.TransformAndAdd(dequant, recY, blockOrigin, yStride);
            }
        }

        int uOrigin = uOff(mbX * 8, mbY * 8);
        int srcUOrigin = (mbY * 8 * srcUvStride) + (mbX * 8);

        int uvMode = Vp8ModeDecision.SelectWholeBlockMode(recU, uOrigin, uvStride, 8, hasAbove, hasLeft, srcU, srcUOrigin, srcUvStride, out _);

        // Chroma shares one mode between U and V (matching Vp8ModeDecoder's single info.UvMode); V's own
        // best-scoring mode is not independently evaluated, only U's prediction is trialed above.
        Vp8IntraPredictionWholeBlock.PredictModeWholeBlock(uvMode, recV, uOrigin, uvStride, 8, hasAbove, hasLeft);

        for (int c = 0; c < 4; c++)
        {
            int row = c / 2;
            int col = c % 2;
            int uBlockOrigin = uOff((mbX * 8) + (col * 4), (mbY * 8) + (row * 4));
            int srcUBlockOrigin = ((mbY * 8 + (row * 4)) * srcUvStride) + (mbX * 8) + (col * 4);

            EncodeChromaBlock(srcU, recU, srcUBlockOrigin, uBlockOrigin, srcUvStride, uvStride, quant, quantizedBlocks, lastBlocks, 16 + c, ref anyNonZero);
            EncodeChromaBlock(srcV, recV, srcUBlockOrigin, uBlockOrigin, srcUvStride, uvStride, quant, quantizedBlocks, lastBlocks, 20 + c, ref anyNonZero);
        }

        bool skip = !anyNonZero;

        modeWriter.WriteMacroblockModes(partition0, mbX, skip, useSkipProbability, skipFalseProbability, isI4x4, wholeMode, subModes, uvMode);

        if (skip)
        {
            ResetSkippedContext(coeffContext, mbX, isI4x4);
        }
        else
        {
            EncodeMacroblockCoefficients(coefficientEncoder, coeffProbabilities, isI4x4, mbX, coeffContext, quantizedBlocks, lastBlocks, y2Quantized, y2Last);
        }
    }

    private static void EncodeChromaBlock(
        byte[] srcPlane, byte[] recPlane, int srcOrigin, int recOrigin, int srcStride, int recStride,
        Vp8QuantMatrix quant, short[][] quantizedBlocks, int[] lastBlocks, int blockIndex, ref bool anyNonZero)
    {
        var raw = new short[16];
        Vp8ForwardDct.Transform(srcPlane, srcOrigin, srcStride, recPlane, recOrigin, recStride, raw);

        var quantized = new short[16];
        int last = Vp8ForwardQuantizer.Quantize(raw, quant.UvDc, quant.UvAc, quantized);
        quantizedBlocks[blockIndex] = quantized;
        lastBlocks[blockIndex] = last;
        if (last > 0)
        {
            anyNonZero = true;
        }

        var dequant = new short[16];
        Vp8ForwardQuantizer.Dequantize(quantized, quant.UvDc, quant.UvAc, dequant);
        Vp8ScalarInverseDct.TransformAndAdd(dequant, recPlane, recOrigin, recStride);
    }

    /// <summary>Entropy-encodes one macroblock's coefficient tokens in the exact order <see cref="Vp8CoefficientDecoder"/> reads them: Y2 (if not B_PRED), then the 16 luma blocks, then chroma U, then chroma V.</summary>
    private static void EncodeMacroblockCoefficients(
        Vp8BoolEncoder tokenBw, byte[] probabilities, bool isI4x4, int mbX,
        Vp8CoefficientContext context, short[][] quantizedBlocks, int[] lastBlocks, short[] y2Quantized, int y2Last)
    {
        if (!isI4x4)
        {
            int dcCtx = (context.LeftDc ? 1 : 0) + (context.AboveDc[mbX] ? 1 : 0);
            Vp8CoefficientEncoder.EncodeBlock(tokenBw, probabilities, 1, dcCtx, 0, y2Quantized, y2Last);
            bool dcNonZero = y2Last > 0;
            context.LeftDc = dcNonZero;
            context.AboveDc[mbX] = dcNonZero;
        }

        int yPlaneType = isI4x4 ? 3 : 0;
        int yFirst = isI4x4 ? 0 : 1;

        Span<bool> colY = stackalloc bool[4];
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
                Vp8CoefficientEncoder.EncodeBlock(tokenBw, probabilities, yPlaneType, ctx, yFirst, quantizedBlocks[idx], lastBlocks[idx]);
                bool nz = lastBlocks[idx] > yFirst;
                rowLeft = nz;
                colY[col] = nz;
            }

            context.LeftY[row] = rowLeft;
        }

        for (int i = 0; i < 4; i++)
        {
            context.AboveY[(mbX * 4) + i] = colY[i];
        }

        EncodeChromaPlaneTokens(tokenBw, probabilities, mbX, context.AboveU, context.LeftU, quantizedBlocks, lastBlocks, blockOffset: 16);
        EncodeChromaPlaneTokens(tokenBw, probabilities, mbX, context.AboveV, context.LeftV, quantizedBlocks, lastBlocks, blockOffset: 20);
    }

    private static void EncodeChromaPlaneTokens(
        Vp8BoolEncoder tokenBw, byte[] probabilities, int mbX, bool[] above, bool[] left,
        short[][] quantizedBlocks, int[] lastBlocks, int blockOffset)
    {
        Span<bool> col = stackalloc bool[2];
        col[0] = above[(mbX * 2) + 0];
        col[1] = above[(mbX * 2) + 1];

        for (int row = 0; row < 2; row++)
        {
            bool rowLeft = left[row];
            for (int c = 0; c < 2; c++)
            {
                int idx = blockOffset + (row * 2) + c;
                int ctx = (rowLeft ? 1 : 0) + (col[c] ? 1 : 0);
                Vp8CoefficientEncoder.EncodeBlock(tokenBw, probabilities, 2, ctx, 0, quantizedBlocks[idx], lastBlocks[idx]);
                bool nz = lastBlocks[idx] > 0;
                rowLeft = nz;
                col[c] = nz;
            }

            left[row] = rowLeft;
        }

        above[(mbX * 2) + 0] = col[0];
        above[(mbX * 2) + 1] = col[1];
    }

    /// <summary>Mirrors <c>Vp8FrameDecoder.ResetSkippedContext</c>: a skipped macroblock still resets the above/left nonzero-coefficient context as if it decoded to all zeros.</summary>
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

    /// <summary>Mirrors <c>Vp8FrameDecoder.ComputeAboveRight</c>: the 4 "above-right" pixels shared by every subblock in a B_PRED macroblock's rightmost column, since subblocks below the top row have no real above-right neighbor of their own.</summary>
    private static ReadOnlySpan<byte> ComputeAboveRight(byte[] yPlane, Func<int, int, int> yOff, int mbX, int mbY, int mbCols, bool hasAbove, Span<byte> aboveRight)
    {
        if (!hasAbove)
        {
            aboveRight.Fill(127);
            return aboveRight;
        }

        if (mbX == mbCols - 1)
        {
            byte value = yPlane[yOff((mbX * 16) + 15, (mbY * 16) - 1)];
            aboveRight.Fill(value);
            return aboveRight;
        }

        int baseOffset = yOff((mbX * 16) + 16, (mbY * 16) - 1);
        for (int k = 0; k < 4; k++)
        {
            aboveRight[k] = yPlane[baseOffset + k];
        }

        return aboveRight;
    }

    /// <summary>Mirrors <c>Vp8FrameDecoder.InitializeBorders</c>: a synthetic 127-valued row above and 129-valued column to the left of the real macroblock grid, so prediction never needs to special-case the frame's real top row/left column.</summary>
    private static void InitializeBorders(byte[] plane, int stride, int paddedHeight)
    {
        plane.AsSpan(0, stride).Fill(127);
        for (int row = 1; row < paddedHeight; row++)
        {
            plane[row * stride] = 129;
        }
    }

    /// <summary>Edge-replicates a real <paramref name="realWidth"/> x <paramref name="realHeight"/> region out to the full <paramref name="paddedWidth"/> x <paramref name="paddedHeight"/> macroblock grid, matching this codebase's clamp-to-edge convention elsewhere (e.g. Jpeg's block-grid padding).</summary>
    private static void PadPlaneToGrid(byte[] plane, int realWidth, int realHeight, int paddedWidth, int paddedHeight)
    {
        for (int y = 0; y < realHeight; y++)
        {
            int rowBase = y * paddedWidth;
            byte edge = plane[rowBase + realWidth - 1];
            for (int x = realWidth; x < paddedWidth; x++)
            {
                plane[rowBase + x] = edge;
            }
        }

        for (int y = realHeight; y < paddedHeight; y++)
        {
            Array.Copy(plane, (realHeight - 1) * paddedWidth, plane, y * paddedWidth, paddedWidth);
        }
    }
}
