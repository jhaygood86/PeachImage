using PeachImage.Formats.Jpeg.Dct;
using PeachImage.Formats.Jpeg.Entropy;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>Decodes a single baseline sequential scan: every coefficient of every block, in one pass.</summary>
internal static class BaselineScanDecoder
{
    /// <summary>Decodes an entire baseline scan, interleaved across <paramref name="scanComponents"/> when there is more than one.</summary>
    public static void DecodeScan(
        JpegEntropyReader entropy,
        ComponentDecodeState[] scanComponents,
        HuffmanDecodingTable[] dcTables,
        HuffmanDecodingTable[] acTables,
        RestartController restart,
        int mcusAcross,
        int mcusDown)
    {
        foreach (var component in scanComponents)
        {
            component.DcPredictor = 0;
        }

        if (scanComponents.Length == 1)
        {
            DecodeNonInterleaved(entropy, scanComponents[0], dcTables[0], acTables[0], restart);
            return;
        }

        for (int mcuY = 0; mcuY < mcusDown; mcuY++)
        {
            for (int mcuX = 0; mcuX < mcusAcross; mcuX++)
            {
                if (restart.ShouldRestart)
                {
                    restart.ConsumeRestart(entropy);
                    foreach (var component in scanComponents)
                    {
                        component.DcPredictor = 0;
                    }
                }

                for (int ci = 0; ci < scanComponents.Length; ci++)
                {
                    var component = scanComponents[ci];
                    for (int by = 0; by < component.BlocksPerMcuVertical; by++)
                    {
                        for (int bx = 0; bx < component.BlocksPerMcuHorizontal; bx++)
                        {
                            int blockX = (mcuX * component.BlocksPerMcuHorizontal) + bx;
                            int blockY = (mcuY * component.BlocksPerMcuVertical) + by;
                            DecodeBlock(entropy, component, dcTables[ci], acTables[ci], blockX, blockY);
                        }
                    }
                }

                restart.NotifyUnitDecoded();
            }
        }
    }

    private static void DecodeNonInterleaved(
        JpegEntropyReader entropy,
        ComponentDecodeState component,
        HuffmanDecodingTable dcTable,
        HuffmanDecodingTable acTable,
        RestartController restart)
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

                DecodeBlock(entropy, component, dcTable, acTable, bx, by);
                restart.NotifyUnitDecoded();
            }
        }
    }

    private static void DecodeBlock(
        JpegEntropyReader entropy,
        ComponentDecodeState component,
        HuffmanDecodingTable dcTable,
        HuffmanDecodingTable acTable,
        int blockX,
        int blockY)
    {
        var block = component.Coefficients.Block(blockX, blockY);
        block.Clear();

        int dcSize = dcTable.Decode(entropy);
        int diff = entropy.ReceiveExtend(dcSize);
        component.DcPredictor += diff;
        block[0] = (short)component.DcPredictor;

        int k = 1;
        while (k <= 63)
        {
            int runSize = acTable.Decode(entropy);
            int size = runSize & 0xF;
            int run = runSize >> 4;

            if (size == 0)
            {
                if (run != 15)
                {
                    break; // EOB
                }

                k += 16; // ZRL: 16 zero coefficients
                continue;
            }

            k += run;
            if (k > 63)
            {
                throw new JpegDecodingException("Invalid JPEG AC coefficient run: index exceeds block size.");
            }

            int value = entropy.ReceiveExtend(size);
            block[ZigZag.ToNaturalOrder[k]] = (short)value;
            k++;
        }
    }
}
