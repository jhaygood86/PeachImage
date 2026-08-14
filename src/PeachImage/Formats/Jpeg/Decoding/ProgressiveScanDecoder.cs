using PeachImage.Formats.Jpeg.Dct;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Decodes a single progressive scan. Dispatches on (Ss, Se, Ah) into the four sub-algorithms defined by
/// ITU-T.81 Annex G: DC-first, DC-refine, AC-first (with EOB run-length coding), and AC-refine (the
/// successive-approximation correction-bit state machine).
/// </summary>
internal static class ProgressiveScanDecoder
{
    /// <summary>Decodes one progressive scan's entropy-coded data into the participating components' coefficient buffers.</summary>
    public static void DecodeScan(
        JpegEntropyReader entropy,
        JpegScanHeader scanHeader,
        ComponentDecodeState[] scanComponents,
        HuffmanDecodingTable[] dcTables,
        HuffmanDecodingTable[] acTables,
        RestartController restart,
        int mcusAcross,
        int mcusDown)
    {
        int ss = scanHeader.SpectralStart;
        int se = scanHeader.SpectralEnd;
        bool isFirst = scanHeader.SuccessiveApproximationHigh == 0;
        int al = scanHeader.SuccessiveApproximationLow;

        foreach (var component in scanComponents)
        {
            component.DcPredictor = 0;
        }

        if (ss == 0)
        {
            if (scanComponents.Length > 1)
            {
                DecodeDcInterleaved(entropy, scanComponents, dcTables, restart, mcusAcross, mcusDown, isFirst, al);
            }
            else
            {
                DecodeDcNonInterleaved(entropy, scanComponents[0], dcTables[0], restart, isFirst, al);
            }
        }
        else
        {
            int eobRun = 0;
            DecodeAcNonInterleaved(entropy, scanComponents[0], acTables[0], restart, ss, se, isFirst, al, ref eobRun);
        }
    }

    private static void DecodeDcInterleaved(
        JpegEntropyReader entropy,
        ComponentDecodeState[] components,
        HuffmanDecodingTable[] dcTables,
        RestartController restart,
        int mcusAcross,
        int mcusDown,
        bool isFirst,
        int al)
    {
        for (int mcuY = 0; mcuY < mcusDown; mcuY++)
        {
            for (int mcuX = 0; mcuX < mcusAcross; mcuX++)
            {
                if (restart.ShouldRestart)
                {
                    restart.ConsumeRestart(entropy);
                    foreach (var component in components)
                    {
                        component.DcPredictor = 0;
                    }
                }

                for (int ci = 0; ci < components.Length; ci++)
                {
                    var component = components[ci];
                    for (int by = 0; by < component.BlocksPerMcuVertical; by++)
                    {
                        for (int bx = 0; bx < component.BlocksPerMcuHorizontal; bx++)
                        {
                            int blockX = (mcuX * component.BlocksPerMcuHorizontal) + bx;
                            int blockY = (mcuY * component.BlocksPerMcuVertical) + by;
                            var block = component.Coefficients.Block(blockX, blockY);

                            if (isFirst)
                            {
                                DecodeDcFirstBlock(entropy, dcTables[ci], component, block, al);
                            }
                            else
                            {
                                DecodeDcRefineBlock(entropy, block, al);
                            }
                        }
                    }
                }

                restart.NotifyUnitDecoded();
            }
        }
    }

    private static void DecodeDcNonInterleaved(
        JpegEntropyReader entropy,
        ComponentDecodeState component,
        HuffmanDecodingTable dcTable,
        RestartController restart,
        bool isFirst,
        int al)
    {
        int blocksWide = (component.ActualWidthInSamples + 7) / 8;
        int blocksHigh = (component.ActualHeightInSamples + 7) / 8;

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                if (restart.ShouldRestart)
                {
                    restart.ConsumeRestart(entropy);
                    component.DcPredictor = 0;
                }

                var block = component.Coefficients.Block(bx, by);
                if (isFirst)
                {
                    DecodeDcFirstBlock(entropy, dcTable, component, block, al);
                }
                else
                {
                    DecodeDcRefineBlock(entropy, block, al);
                }

                restart.NotifyUnitDecoded();
            }
        }
    }

    private static void DecodeAcNonInterleaved(
        JpegEntropyReader entropy,
        ComponentDecodeState component,
        HuffmanDecodingTable acTable,
        RestartController restart,
        int ss,
        int se,
        bool isFirst,
        int al,
        ref int eobRun)
    {
        int blocksWide = (component.ActualWidthInSamples + 7) / 8;
        int blocksHigh = (component.ActualHeightInSamples + 7) / 8;

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                if (restart.ShouldRestart)
                {
                    restart.ConsumeRestart(entropy);
                    eobRun = 0;
                }

                var block = component.Coefficients.Block(bx, by);
                if (isFirst)
                {
                    DecodeAcFirstBlock(entropy, acTable, block, ss, se, al, ref eobRun);
                }
                else
                {
                    DecodeAcRefineBlock(entropy, acTable, block, ss, se, al, ref eobRun);
                }

                restart.NotifyUnitDecoded();
            }
        }
    }

    private static void DecodeDcFirstBlock(JpegEntropyReader entropy, HuffmanDecodingTable dcTable, ComponentDecodeState component, Span<short> block, int al)
    {
        int diff = dcTable.DecodeDcDiff(entropy);
        component.DcPredictor += diff;
        block[0] = (short)(component.DcPredictor << al);
    }

    private static void DecodeDcRefineBlock(JpegEntropyReader entropy, Span<short> block, int al)
    {
        if (entropy.ReceiveBit() != 0)
        {
            block[0] |= (short)(1 << al);
        }
    }

    private static void DecodeAcFirstBlock(
        JpegEntropyReader entropy,
        HuffmanDecodingTable acTable,
        Span<short> block,
        int ss,
        int se,
        int al,
        ref int eobRun)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }

        int k = ss;
        while (k <= se)
        {
            int runSize = acTable.Decode(entropy);
            int size = runSize & 0xF;
            int run = runSize >> 4;

            if (size == 0)
            {
                if (run != 15)
                {
                    eobRun = (1 << run) - 1;
                    if (run > 0)
                    {
                        eobRun += entropy.GetBits(run);
                    }

                    break;
                }

                k += 16;
                continue;
            }

            k += run;
            if (k > se)
            {
                throw new JpegDecodingException("Invalid JPEG progressive AC run: index exceeds spectral range.");
            }

            int value = entropy.ReceiveExtend(size);
            block[ZigZag.ToNaturalOrder[k]] = (short)(value << al);
            k++;
        }
    }

    private static void DecodeAcRefineBlock(
        JpegEntropyReader entropy,
        HuffmanDecodingTable acTable,
        Span<short> block,
        int ss,
        int se,
        int al,
        ref int eobRun)
    {
        short p1 = (short)(1 << al);
        short m1 = (short)-(1 << al);

        int k = ss;

        if (eobRun == 0)
        {
            while (k <= se)
            {
                int runSize = acTable.Decode(entropy);
                int size = runSize & 0xF;
                int run = runSize >> 4;
                int newValue = 0;

                if (size == 0)
                {
                    if (run != 15)
                    {
                        eobRun = 1 << run;
                        if (run > 0)
                        {
                            eobRun += entropy.GetBits(run);
                        }

                        break;
                    }

                    // run == 15 (ZRL): fall through to the skip-run loop with run=15, newValue=0.
                }
                else
                {
                    // In a refinement scan, size is always 1: the single bit read is the new coefficient's sign.
                    newValue = entropy.ReceiveBit() != 0 ? p1 : m1;
                }

                while (k <= se)
                {
                    int naturalIndex = ZigZag.ToNaturalOrder[k];
                    if (block[naturalIndex] != 0)
                    {
                        if (entropy.ReceiveBit() != 0 && (block[naturalIndex] & p1) == 0)
                        {
                            block[naturalIndex] += (short)(block[naturalIndex] >= 0 ? p1 : m1);
                        }
                    }
                    else
                    {
                        if (run == 0)
                        {
                            if (newValue != 0)
                            {
                                block[naturalIndex] = (short)newValue;
                            }

                            k++;
                            break;
                        }

                        run--;
                    }

                    k++;
                }
            }
        }

        if (eobRun > 0)
        {
            while (k <= se)
            {
                int naturalIndex = ZigZag.ToNaturalOrder[k];
                if (block[naturalIndex] != 0 && entropy.ReceiveBit() != 0 && (block[naturalIndex] & p1) == 0)
                {
                    block[naturalIndex] += (short)(block[naturalIndex] >= 0 ? p1 : m1);
                }

                k++;
            }

            eobRun--;
        }
    }
}
