using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Jpeg;

namespace PeachImage.Formats.Jxl.VarDct;

/// <summary>
/// JPEG reconstruction mode of a VarDCT frame that holds a recompressed JPEG: instead of dequantizing and transforming the
/// blocks, their quantized coefficients are stored (in JPEG layout) in the <see cref="JxlJpegData"/> being rebuilt.
/// </summary>
internal sealed partial class VarDctFrame
{
    private const int CflFixedPointPrecision = 11;
    private const int DefaultColorFactor = 84;
    private const int MaxDctCoefficient = 4095;

    private JxlJpegData? _jpeg;
    private bool _jpegGray;
    private int[] _jpegMap = [1, 0, 2];
    private int[] _jpegScaledQuantTable = [];
    private readonly int[] _jpegDcOffset = new int[3];
    private bool _jpegPrepared;

    /// <summary>Whether the frame's blocks are being captured as JPEG coefficients.</summary>
    public bool IsJpegCapture => _jpeg is not null;

    /// <summary>Switches the frame to JPEG reconstruction mode, sizing the components of <paramref name="jpeg"/> from the frame.</summary>
    public void EnableJpegCapture(JxlJpegData jpeg)
    {
        int count = jpeg.Components.Count;
        if (count != 1 && count != 3)
        {
            throw new JxlDecodingException("Invalid number of JPEG components.");
        }

        if (_frame.ColorTransform == JxlColorTransform.Xyb)
        {
            throw new JxlUnsupportedFeatureException("An XYB-coded frame cannot be converted back to a JPEG.");
        }

        _jpeg = jpeg;
        _jpegGray = count == 1;

        // Blocks and quantization tables follow the frame's colour transform; the component sizes always use the YCbCr order, as
        // the reference decoder does (the two only differ for subsampled RGB, which a recompressed JPEG cannot be).
        _jpegMap = _jpegGray ? [0, 0, 0] : _frame.ColorTransform == JxlColorTransform.None ? [0, 1, 2] : [1, 0, 2];
        int[] sizeMap = _jpegGray ? [0, 0, 0] : [1, 0, 2];
        jpeg.Width = _dims.XSize;
        jpeg.Height = _dims.YSize;
        for (int c = 0; c < count; c++)
        {
            var component = jpeg.Components[sizeMap[c]];
            component.WidthInBlocks = (uint)(_blocksX >> _hShift[c]);
            component.HeightInBlocks = (uint)(_blocksY >> _vShift[c]);
            int mode = _frame.ChromaSubsamplingModes[c];
            component.HorizontalSamplingFactor = 1 << ModeHorizontalShift[mode];
            component.VerticalSamplingFactor = 1 << ModeVerticalShift[mode];
            long coefficients = (long)component.WidthInBlocks * component.HeightInBlocks * 64;
            if (coefficients > int.MaxValue / 2)
            {
                throw new JxlDecodingException("The JPEG is too large.");
            }

            component.Coefficients = new short[coefficients];
        }
    }

    // Checks that the frame really is a lossless JPEG recompression and derives the tables the block capture needs.
    private void PrepareJpeg()
    {
        if (_jpegPrepared)
        {
            return;
        }

        var jpeg = _jpeg!;
        if (!Correlation.IsJpegCompatible)
        {
            throw new JxlDecodingException("The chroma-from-luma map is not JPEG-compatible.");
        }

        var encoding = Matrices.Encodings[0];
        if (encoding.Mode != QuantMode.Raw || Math.Abs(encoding.RawDenominator - (1f / (8 * 255))) > 1e-8f || encoding.RawTable is not { Length: 3 * 64 } table)
        {
            throw new JxlDecodingException("The quantization table is not a JPEG quantization table.");
        }

        int count = jpeg.Components.Count;
        int tablesSet = 0;
        for (int c = 0; c < count; c++)
        {
            int index = (int)jpeg.Components[_jpegMap[c]].QuantTableIndex;
            int source = _jpegGray ? 1 : c;
            tablesSet |= 1 << index;
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    jpeg.Quant[index].Values[(x * 8) + y] = table[(source * 64) + (y * 8) + x];
                }
            }
        }

        for (int i = 0; i < jpeg.Quant.Count; i++)
        {
            if ((tablesSet & (1 << i)) != 0)
            {
                continue;
            }

            if (i == 0)
            {
                throw new JxlDecodingException("The first quantization table is unused.");
            }

            Array.Copy(jpeg.Quant[i - 1].Values, jpeg.Quant[i].Values, 64);
        }

        _jpegScaledQuantTable = new int[3 * 64];
        for (int c = 0; c < 3; c++)
        {
            if (_frame.ColorTransform == JxlColorTransform.None)
            {
                _jpegDcOffset[c] = 1024 / table[64 * c];
            }

            for (int i = 0; i < 64; i++)
            {
                // The matrix is transposed because it is used on transposed blocks.
                int numerator = table[64 + i];
                int denominator = table[(64 * c) + i];
                if (numerator <= 0 || denominator <= 0 || numerator >= 65536 || denominator >= 65536)
                {
                    throw new JxlDecodingException("Invalid JPEG quantization table.");
                }

                _jpegScaledQuantTable[(64 * c) + ((i % 8) * 8) + (i / 8)] = (1 << CflFixedPointPrecision) * numerator / denominator;
            }
        }

        _jpegPrepared = true;
    }

    // Stores one 8x8 block (all present channels) at absolute block position (blockX, blockY).
    private void CaptureJpegBlock(int blockX, int blockY, int[][] quantized, bool[] present)
    {
        var jpeg = _jpeg!;
        int tile = ((blockY / ColorCorrelation.TileDimInBlocks) * _tilesX) + (blockX / ColorCorrelation.TileDimInBlocks);
        int ytox = _yToX[tile];
        int ytob = _yToB[tile];
        bool cfl = IsFullResolution && (ytox != 0 || ytob != 0);
        Span<int> luma = stackalloc int[64];

        foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
        {
            if ((_jpegGray && c != 1) || !present[c])
            {
                continue;
            }

            int sbx = blockX >> _hShift[c];
            int sby = blockY >> _vShift[c];
            var component = jpeg.Components[_jpegMap[c]];
            long position = (((long)sby * component.WidthInBlocks) + sbx) * 64;
            var destination = component.Coefficients.AsSpan((int)position, 64);

            // The JPEG XL block is the transpose of the JPEG one.
            var block = quantized[c].AsSpan(0, 64);
            Transpose8x8(block);
            if (!cfl)
            {
                for (int i = 0; i < 64; i++)
                {
                    destination[i] = Saturate(block[i]);
                }
            }
            else if (c == 1)
            {
                block.CopyTo(luma);
                for (int i = 0; i < 64; i++)
                {
                    destination[i] = Saturate(block[i]);
                }
            }
            else
            {
                int factor = c == 0 ? ytox : ytob;
                int scale = factor * (1 << CflFixedPointPrecision) / DefaultColorFactor;
                const int Round = 1 << (CflFixedPointPrecision - 1);
                for (int i = 0; i < 64; i++)
                {
                    int quant = _jpegScaledQuantTable[(c * 64) + i];
                    int coefficientScale = ((quant * scale) + Round) >> CflFixedPointPrecision;
                    int cflFactor = ((luma[i] * coefficientScale) + Round) >> CflFixedPointPrecision;
                    destination[i] = Saturate(block[i] + cflFactor);
                }
            }

            float dc = _dc[c][(sby * _dcStride[c]) + sbx];
            destination[0] = (short)Math.Clamp(dc - _jpegDcOffset[c], -2047f, 2047f);
            foreach (short value in destination)
            {
                if (value > MaxDctCoefficient || value < -MaxDctCoefficient)
                {
                    throw new JxlDecodingException("JPEG DCT coefficients out of range.");
                }
            }
        }
    }

    private static short Saturate(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static void Transpose8x8(Span<int> block)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int x = y + 1; x < 8; x++)
            {
                (block[(y * 8) + x], block[(x * 8) + y]) = (block[(x * 8) + y], block[(y * 8) + x]);
            }
        }
    }
}
