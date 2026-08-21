using System.Buffers;
using PeachImage.Formats.Jpeg.Dct;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Formats.Jpeg.Encoding;

/// <summary>
/// Encodes a single progressive scan. Mirrors <see cref="Decoding.ProgressiveScanDecoder"/>'s dispatch on
/// (Ss, Ah) into the four sub-algorithms defined by ITU-T.81 Annex G: DC-first, DC-refine, AC-first (with
/// EOB run-length coding), and AC-refine (the successive-approximation correction-bit state machine). Every
/// formula below (EOB run encoding, the point-transform conventions, the already/newly-significant
/// classification) is derived directly from that decoder and must stay bit-for-bit compatible with it.
/// </summary>
/// <remarks>
/// AC scans (first or refine) build and write their own Huffman table per scan rather than reusing
/// <see cref="StandardHuffmanTables"/>: those standard tables were tuned for baseline symbol distributions
/// and simply have no entries for progressive's EOB-run-length symbols (run lengths 1-14 at size 0) — an
/// <see cref="HuffmanEncodingTable"/> built from them silently encodes any missing symbol as a zero-length
/// (no-op) write, corrupting the stream. Each AC scan therefore runs its own <see cref="EncodeAcFirstScan"/>
/// or <see cref="EncodeAcRefineScan"/> body twice: once counting symbol frequencies into a throwaway sink,
/// then for real against a table built from those counts via <see cref="HuffmanTableOptimizer"/>. DC scans
/// don't need this — DC's size-category symbols (0-11) are always a subset of the standard DC tables' full
/// range, standard or progressive.
/// </remarks>
internal static class ProgressiveScanEncoder
{
    // The AC run field is 4 bits but 0xF (run=15) is reserved for ZRL, so the largest representable
    // eob-run value is bounded (run<=14, giving up to ~32K); capping well below that and flushing early
    // just keeps the accumulation loop from ever needing to reason about the boundary.
    private const int EobRunSafetyCap = 16000;

    // Fixed AC table id progressive scans redefine before every use — each AC scan is entirely
    // self-contained (built, used once, never referenced by a later scan), so a single id suffices.
    private const int ProgressiveAcTableId = 0;

    public static void EncodeScan(
        JpegEntropyWriter writer,
        Stream stream,
        ScanDescriptor scan,
        FrameEncoder.ComponentPlan[] components,
        short[][] quantizedComponents,
        HuffmanEncodingTable[] dcTables,
        int restartInterval,
        int mcusAcross,
        int mcusDown)
    {
        if (scan.Ss == 0)
        {
            FrameEncoder.WriteScanHeader(stream, components, scan.ComponentIndices, scan.Ss, scan.Se, scan.Ah, scan.Al);
            EncodeDcScan(writer, stream, scan, components, quantizedComponents, dcTables, restartInterval, mcusAcross, mcusDown);
        }
        else
        {
            int ci = scan.ComponentIndices[0];
            var plan = components[ci];
            var coefficients = quantizedComponents[ci];

            if (scan.Ah == 0)
            {
                var frequencies = new int[256];
                EncodeAcFirstScan(new JpegEntropyWriter(Stream.Null), Stream.Null, scan, plan, coefficients, sym => frequencies[sym]++, restartInterval);
                var table = BuildAndWriteOptimizedAcTable(stream, frequencies);
                FrameEncoder.WriteScanHeader(stream, components, scan.ComponentIndices, scan.Ss, scan.Se, scan.Ah, scan.Al, ProgressiveAcTableId);
                EncodeAcFirstScan(writer, stream, scan, plan, coefficients, sym => table.Encode(writer, sym), restartInterval);
            }
            else
            {
                var frequencies = new int[256];
                EncodeAcRefineScan(new JpegEntropyWriter(Stream.Null), Stream.Null, scan, plan, coefficients, sym => frequencies[sym]++, restartInterval);
                var table = BuildAndWriteOptimizedAcTable(stream, frequencies);
                FrameEncoder.WriteScanHeader(stream, components, scan.ComponentIndices, scan.Ss, scan.Se, scan.Ah, scan.Al, ProgressiveAcTableId);
                EncodeAcRefineScan(writer, stream, scan, plan, coefficients, sym => table.Encode(writer, sym), restartInterval);
            }
        }
    }

    private static HuffmanEncodingTable BuildAndWriteOptimizedAcTable(Stream stream, int[] frequencies)
    {
        var (counts, values) = HuffmanTableOptimizer.Build(frequencies);
        var table = HuffmanEncodingTable.Build(counts, values);
        FrameEncoder.WriteHuffmanTable(stream, tableClass: 1, id: ProgressiveAcTableId, table);
        return table;
    }

    private static void EncodeDcScan(
        JpegEntropyWriter writer,
        Stream stream,
        ScanDescriptor scan,
        FrameEncoder.ComponentPlan[] components,
        short[][] quantizedComponents,
        HuffmanEncodingTable[] dcTables,
        int restartInterval,
        int mcusAcross,
        int mcusDown)
    {
        var indices = scan.ComponentIndices;
        var predictors = new int[indices.Length];
        int al = scan.Al;
        bool isFirst = scan.Ah == 0;
        int mcusUntilRestart = restartInterval;
        int restartIndex = 0;

        for (int mcuY = 0; mcuY < mcusDown; mcuY++)
        {
            for (int mcuX = 0; mcuX < mcusAcross; mcuX++)
            {
                if (restartInterval > 0 && mcusUntilRestart == 0)
                {
                    writer.Flush();
                    JpegMarkerWriter.WriteMarkerOnly(stream, (JpegMarker)(0xD0 + restartIndex));
                    writer.Reset();
                    Array.Clear(predictors);
                    restartIndex = (restartIndex + 1) & 7;
                    mcusUntilRestart = restartInterval;
                }

                for (int pi = 0; pi < indices.Length; pi++)
                {
                    int ci = indices[pi];
                    var plan = components[ci];
                    var coefficients = quantizedComponents[ci];
                    for (int by = 0; by < plan.VSampling; by++)
                    {
                        for (int bx = 0; bx < plan.HSampling; bx++)
                        {
                            int blockX = (mcuX * plan.HSampling) + bx;
                            int blockY = (mcuY * plan.VSampling) + by;
                            int blockOffset = ((blockY * plan.BlocksWide) + blockX) * 64;
                            int trueDc = coefficients[blockOffset];

                            if (isFirst)
                            {
                                int transformed = trueDc >> al;
                                int diff = transformed - predictors[pi];
                                predictors[pi] = transformed;

                                int size = FrameEncoder.MagnitudeBits(diff);
                                dcTables[ci].Encode(writer, (byte)size);
                                if (size > 0)
                                {
                                    writer.WriteBits(FrameEncoder.EncodeMagnitude(diff, size), size);
                                }
                            }
                            else
                            {
                                writer.WriteBits((trueDc >> al) & 1, 1);
                            }
                        }
                    }
                }

                if (restartInterval > 0)
                {
                    mcusUntilRestart--;
                }
            }
        }
    }

    private static void EncodeAcFirstScan(
        JpegEntropyWriter writer,
        Stream stream,
        ScanDescriptor scan,
        FrameEncoder.ComponentPlan plan,
        short[] coefficients,
        Action<byte> emitSymbol,
        int restartInterval)
    {
        int ss = scan.Ss, se = scan.Se, al = scan.Al;
        int pendingEobRun = 0;
        bool haveTrigger = false;
        int unitsUntilRestart = restartInterval;
        int restartIndex = 0;

        for (int by = 0; by < plan.ActualBlocksHigh; by++)
        {
            for (int bx = 0; bx < plan.ActualBlocksWide; bx++)
            {
                if (restartInterval > 0 && unitsUntilRestart == 0)
                {
                    FlushAcFirstEobRun(writer, emitSymbol, ref pendingEobRun, ref haveTrigger);
                    writer.Flush();
                    JpegMarkerWriter.WriteMarkerOnly(stream, (JpegMarker)(0xD0 + restartIndex));
                    writer.Reset();
                    restartIndex = (restartIndex + 1) & 7;
                    unitsUntilRestart = restartInterval;
                }

                int blockOffset = ((by * plan.BlocksWide) + bx) * 64;
                var block = coefficients.AsSpan(blockOffset, 64);

                bool foldIntoRun = false;
                if (haveTrigger)
                {
                    if (IsAcRangeAllZero(block, ss, se, al))
                    {
                        pendingEobRun++;
                        if (pendingEobRun >= EobRunSafetyCap)
                        {
                            FlushAcFirstEobRun(writer, emitSymbol, ref pendingEobRun, ref haveTrigger);
                        }

                        foldIntoRun = true;
                    }
                    else
                    {
                        FlushAcFirstEobRun(writer, emitSymbol, ref pendingEobRun, ref haveTrigger);
                    }
                }

                if (!foldIntoRun)
                {
                    bool trailingZero = EncodeAcFirstBlock(writer, emitSymbol, block, ss, se, al);
                    if (trailingZero)
                    {
                        haveTrigger = true;
                        pendingEobRun = 0;
                    }
                }

                if (restartInterval > 0)
                {
                    unitsUntilRestart--;
                }
            }
        }

        FlushAcFirstEobRun(writer, emitSymbol, ref pendingEobRun, ref haveTrigger);
    }

    private static bool EncodeAcFirstBlock(JpegEntropyWriter writer, Action<byte> emitSymbol, Span<short> block, int ss, int se, int al)
    {
        int r = 0;
        for (int k = ss; k <= se; k++)
        {
            int v = AcPointTransform(block[ZigZag.ToNaturalOrder[k]], al);
            if (v == 0)
            {
                r++;
                continue;
            }

            while (r > 15)
            {
                emitSymbol(0xF0);
                r -= 16;
            }

            int size = FrameEncoder.MagnitudeBits(v);
            emitSymbol((byte)((r << 4) | size));
            writer.WriteBits(FrameEncoder.EncodeMagnitude(v, size), size);
            r = 0;
        }

        return r > 0;
    }

    private static bool IsAcRangeAllZero(ReadOnlySpan<short> block, int ss, int se, int al)
    {
        for (int k = ss; k <= se; k++)
        {
            if (AcPointTransform(block[ZigZag.ToNaturalOrder[k]], al) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void FlushAcFirstEobRun(JpegEntropyWriter writer, Action<byte> emitSymbol, ref int pendingEobRun, ref bool haveTrigger)
    {
        if (!haveTrigger)
        {
            return;
        }

        int run = FrameEncoder.MagnitudeBits(pendingEobRun + 1) - 1;
        int extra = pendingEobRun - ((1 << run) - 1);
        emitSymbol((byte)(run << 4));
        if (run > 0)
        {
            writer.WriteBits(extra, run);
        }

        pendingEobRun = 0;
        haveTrigger = false;
    }

    private static void EncodeAcRefineScan(
        JpegEntropyWriter writer,
        Stream stream,
        ScanDescriptor scan,
        FrameEncoder.ComponentPlan plan,
        short[] coefficients,
        Action<byte> emitSymbol,
        int restartInterval)
    {
        int ss = scan.Ss, se = scan.Se, ah = scan.Ah, al = scan.Al;
        int span = se - ss + 1;
        int blocksWide = plan.ActualBlocksWide;
        int blocksHigh = plan.ActualBlocksHigh;

        // Upper bound: every coefficient in every block of this component could need a correction bit.
        // Rented/returned locally since the buffer's lifetime never needs to outlive this one scan.
        byte[] correctionBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, blocksWide * blocksHigh * span));
        try
        {
            int correctionCount = 0;
            int pendingEobRun = 0;
            bool haveTrigger = false;
            int unitsUntilRestart = restartInterval;
            int restartIndex = 0;

            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    if (restartInterval > 0 && unitsUntilRestart == 0)
                    {
                        FlushAcRefineEobRun(writer, emitSymbol, correctionBuffer, ref correctionCount, ref pendingEobRun, ref haveTrigger);
                        writer.Flush();
                        JpegMarkerWriter.WriteMarkerOnly(stream, (JpegMarker)(0xD0 + restartIndex));
                        writer.Reset();
                        restartIndex = (restartIndex + 1) & 7;
                        unitsUntilRestart = restartInterval;
                    }

                    int blockOffset = ((by * plan.BlocksWide) + bx) * 64;
                    var block = coefficients.AsSpan(blockOffset, 64);

                    bool foldIntoRun = false;
                    if (haveTrigger)
                    {
                        if (HasNewSignificantCoefficient(block, ss, se, ah, al))
                        {
                            FlushAcRefineEobRun(writer, emitSymbol, correctionBuffer, ref correctionCount, ref pendingEobRun, ref haveTrigger);
                        }
                        else
                        {
                            BufferCorrectionBits(block, ss, se, ah, al, correctionBuffer, ref correctionCount);
                            pendingEobRun++;
                            if (pendingEobRun >= EobRunSafetyCap)
                            {
                                FlushAcRefineEobRun(writer, emitSymbol, correctionBuffer, ref correctionCount, ref pendingEobRun, ref haveTrigger);
                            }

                            foldIntoRun = true;
                        }
                    }

                    if (!foldIntoRun)
                    {
                        bool trailingQuiet = EncodeAcRefineBlock(writer, emitSymbol, block, ss, se, ah, al, correctionBuffer, ref correctionCount);
                        if (trailingQuiet)
                        {
                            haveTrigger = true;
                            pendingEobRun = 1;
                        }
                    }

                    if (restartInterval > 0)
                    {
                        unitsUntilRestart--;
                    }
                }
            }

            FlushAcRefineEobRun(writer, emitSymbol, correctionBuffer, ref correctionCount, ref pendingEobRun, ref haveTrigger);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(correctionBuffer);
        }
    }

    private static bool EncodeAcRefineBlock(
        JpegEntropyWriter writer,
        Action<byte> emitSymbol,
        Span<short> block,
        int ss,
        int se,
        int ah,
        int al,
        byte[] correctionBuffer,
        ref int correctionCount)
    {
        int r = 0;
        for (int k = ss; k <= se; k++)
        {
            int naturalIndex = ZigZag.ToNaturalOrder[k];
            short trueCoeff = block[naturalIndex];

            if (AcPointTransform(trueCoeff, ah) != 0)
            {
                // Already significant from an earlier scan for this band: buffer this scan's one
                // correction bit (the magnitude bit at position Al), flushed alongside the next symbol.
                correctionBuffer[correctionCount++] = (byte)((Math.Abs((int)trueCoeff) >> al) & 1);
                continue;
            }

            int transformed = AcPointTransform(trueCoeff, al);
            if (transformed == 0)
            {
                r++;
                if (r == 16)
                {
                    // A ZRL only ever represents exactly the 16 true-zero coefficients seen since the
                    // last symbol, so its correction-bit flush must happen right here — deferring it
                    // until a later placement is found would attribute corrections from a *later* 16-span
                    // (or the final remainder) to this ZRL, desyncing the decoder's bit cursor.
                    emitSymbol(0xF0);
                    FlushCorrectionBuffer(writer, correctionBuffer, ref correctionCount);
                    r = 0;
                }

                continue;
            }

            emitSymbol((byte)((r << 4) | 1));
            writer.WriteBits(trueCoeff >= 0 ? 1 : 0, 1);
            FlushCorrectionBuffer(writer, correctionBuffer, ref correctionCount);
            r = 0;
        }

        // "Trailing quiet" (defer to the caller's pendingEobRun/EOB-run mechanism) covers not just a
        // trailing true-zero run (r>0) but also a trailing run of nothing-but-already-significant
        // coefficients (r==0, correctionCount>0): either way, the decoder's outer symbol-decode loop
        // still has k<=se left to account for after the last emitted symbol, and it always decodes
        // *one more Huffman symbol* there (never falls straight through to reading raw correction bits)
        // — so this remainder can never be resolved with a bare bit-flush and must go through an actual
        // EOBn (self-contained or merged with later blocks), exactly like the r>0 case.
        return r > 0 || correctionCount > 0;
    }

    private static bool HasNewSignificantCoefficient(ReadOnlySpan<short> block, int ss, int se, int ah, int al)
    {
        for (int k = ss; k <= se; k++)
        {
            short coeff = block[ZigZag.ToNaturalOrder[k]];
            if (AcPointTransform(coeff, ah) == 0 && AcPointTransform(coeff, al) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void BufferCorrectionBits(ReadOnlySpan<short> block, int ss, int se, int ah, int al, byte[] correctionBuffer, ref int correctionCount)
    {
        for (int k = ss; k <= se; k++)
        {
            short coeff = block[ZigZag.ToNaturalOrder[k]];
            if (AcPointTransform(coeff, ah) != 0)
            {
                correctionBuffer[correctionCount++] = (byte)((Math.Abs((int)coeff) >> al) & 1);
            }
        }
    }

    private static void FlushCorrectionBuffer(JpegEntropyWriter writer, byte[] correctionBuffer, ref int correctionCount)
    {
        for (int i = 0; i < correctionCount; i++)
        {
            writer.WriteBits(correctionBuffer[i], 1);
        }

        correctionCount = 0;
    }

    private static void FlushAcRefineEobRun(
        JpegEntropyWriter writer,
        Action<byte> emitSymbol,
        byte[] correctionBuffer,
        ref int correctionCount,
        ref int pendingEobRun,
        ref bool haveTrigger)
    {
        if (!haveTrigger)
        {
            return;
        }

        int run = FrameEncoder.MagnitudeBits(pendingEobRun) - 1;
        int extra = pendingEobRun - (1 << run);
        emitSymbol((byte)(run << 4));
        if (run > 0)
        {
            writer.WriteBits(extra, run);
        }

        FlushCorrectionBuffer(writer, correctionBuffer, ref correctionCount);
        pendingEobRun = 0;
        haveTrigger = false;
    }

    // AC coefficient point transform: shift the magnitude, then reapply sign (round-toward-zero) — the
    // ITU-T.81 G.1.2.1 convention. Deliberately different from DC's plain arithmetic shift (used inline in
    // EncodeDcScan): a coefficient whose magnitude is too small to be significant at this Al must round to
    // exactly zero (a "true zero" contributing to a run), which a floor-shift of a negative value would not.
    private static int AcPointTransform(int value, int al) => value >= 0 ? value >> al : -((-value) >> al);
}
