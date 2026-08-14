using PeachImage.Formats.Jpeg.ColorConversion;
using PeachImage.Formats.Jpeg.Dct;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Formats.Jpeg.Encoding;

/// <summary>
/// Encodes an <see cref="Image"/> as a baseline sequential JPEG: color-converts and (optionally) subsamples
/// chroma, forward-DCTs and quantizes every block, and writes standard-Huffman-coded entropy data.
/// Supports grayscale and YCbCr (RGB/RGBA source) output; CMYK/YCCK encode is not implemented in v1.
/// </summary>
internal static class FrameEncoder
{
    /// <summary>Encodes <paramref name="image"/> and writes the result to <paramref name="stream"/>.</summary>
    public static void Encode(Image image, Stream stream, JpegEncoderOptions options)
    {
        if (image.PixelFormat is not (PixelFormat.Gray8 or PixelFormat.Rgb24 or PixelFormat.Rgba32))
        {
            throw new JpegEncodingException($"Encoding {image.PixelFormat} images is not supported. Supported source pixel formats are Gray8, Rgb24, and Rgba32.");
        }

        int width = image.Width;
        int height = image.Height;
        bool grayscale = image.PixelFormat == PixelFormat.Gray8;

        byte[] yPlane;
        byte[]? cbPlane = null;
        byte[]? crPlane = null;

        if (grayscale)
        {
            yPlane = image.GetPixelSpan().ToArray();
        }
        else
        {
            byte[] rgb = image.PixelFormat == PixelFormat.Rgba32 ? StripAlpha(image) : image.GetPixelSpan().ToArray();
            yPlane = new byte[width * height];
            cbPlane = new byte[width * height];
            crPlane = new byte[width * height];
            ColorConverterSelector.Instance.RgbToYCbCr(rgb, yPlane, cbPlane, crPlane, width * height);
        }

        (int chromaHRatio, int chromaVRatio) = grayscale ? (1, 1) : GetRatios(options.Subsampling);
        int hMax = grayscale ? 1 : chromaHRatio;
        int vMax = grayscale ? 1 : chromaVRatio;

        int mcuWidth = 8 * hMax;
        int mcuHeight = 8 * vMax;
        int mcusAcross = (width + mcuWidth - 1) / mcuWidth;
        int mcusDown = (height + mcuHeight - 1) / mcuHeight;

        var luminanceQuant = QuantizationTableFactory.CreateLuminance(options.Quality);
        var chrominanceQuant = grayscale ? null : QuantizationTableFactory.CreateChrominance(options.Quality);

        var components = BuildComponentPlans(grayscale, chromaHRatio, chromaVRatio, mcusAcross, mcusDown);

        var quantizedComponents = new short[components.Length][];
        for (int i = 0; i < components.Length; i++)
        {
            var plan = components[i];
            byte[] sourcePlane = plan.Index switch
            {
                0 => yPlane,
                1 => cbPlane!,
                2 => crPlane!,
                _ => throw new InvalidOperationException("Unreachable: JPEG frames encoded here have at most 3 components."),
            };

            int sourceWidth = width;
            int sourceHeight = height;
            if (plan.Index != 0 && (chromaHRatio != 1 || chromaVRatio != 1))
            {
                sourcePlane = ChromaDownsampler.Downsample(sourcePlane, width, height, chromaHRatio, chromaVRatio, out sourceWidth, out sourceHeight);
            }

            var padded = PadToBlockGrid(sourcePlane, sourceWidth, sourceHeight, plan.BlocksWide, plan.BlocksHigh);
            quantizedComponents[i] = QuantizeComponent(padded, plan.BlocksWide, plan.BlocksHigh, plan.Index == 0 ? luminanceQuant : chrominanceQuant!);
        }

        WriteBitstream(stream, options, width, height, grayscale, components, quantizedComponents, luminanceQuant, chrominanceQuant, mcusAcross, mcusDown);
    }

    private static byte[] StripAlpha(Image image)
    {
        var source = image.GetPixelSpan();
        int pixelCount = image.Width * image.Height;
        var rgb = new byte[pixelCount * 3];
        for (int i = 0; i < pixelCount; i++)
        {
            source.Slice(i * 4, 3).CopyTo(rgb.AsSpan(i * 3, 3));
        }

        return rgb;
    }

    private static (int Horizontal, int Vertical) GetRatios(JpegChromaSubsampling subsampling) => subsampling switch
    {
        JpegChromaSubsampling.Yuv444 => (1, 1),
        JpegChromaSubsampling.Yuv422 => (2, 1),
        JpegChromaSubsampling.Yuv420 => (2, 2),
        JpegChromaSubsampling.Yuv411 => (4, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(subsampling)),
    };

    private static ComponentPlan[] BuildComponentPlans(bool grayscale, int chromaHRatio, int chromaVRatio, int mcusAcross, int mcusDown)
    {
        if (grayscale)
        {
            return [new ComponentPlan(0, 1, 1, 1, 0, 0, mcusAcross, mcusDown)];
        }

        return
        [
            new ComponentPlan(0, 1, (byte)chromaHRatio, (byte)chromaVRatio, 0, 0, mcusAcross * chromaHRatio, mcusDown * chromaVRatio),
            new ComponentPlan(1, 2, 1, 1, 1, 1, mcusAcross, mcusDown),
            new ComponentPlan(2, 3, 1, 1, 1, 1, mcusAcross, mcusDown),
        ];
    }

    private static byte[] PadToBlockGrid(byte[] plane, int width, int height, int blocksWide, int blocksHigh)
    {
        int paddedWidth = blocksWide * 8;
        int paddedHeight = blocksHigh * 8;
        if (paddedWidth == width && paddedHeight == height)
        {
            return plane;
        }

        var padded = new byte[paddedWidth * paddedHeight];
        for (int y = 0; y < paddedHeight; y++)
        {
            int sy = Math.Min(y, height - 1);
            for (int x = 0; x < paddedWidth; x++)
            {
                int sx = Math.Min(x, width - 1);
                padded[(y * paddedWidth) + x] = plane[(sy * width) + sx];
            }
        }

        return padded;
    }

    private static short[] QuantizeComponent(byte[] padded, int blocksWide, int blocksHigh, ushort[] quantTable)
    {
        int stride = blocksWide * 8;
        var coefficients = new short[blocksWide * blocksHigh * 64];
        Span<double> fdctOutput = stackalloc double[64];
        var effectiveQuant = DctKernelSelector.Forward.PrepareQuantTable(quantTable);

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int offset = (by * 8 * stride) + (bx * 8);
                DctKernelSelector.Forward.Transform(padded.AsSpan(offset), stride, fdctOutput);

                int blockOffset = ((by * blocksWide) + bx) * 64;
                for (int i = 0; i < 64; i++)
                {
                    double quantized = fdctOutput[i] / effectiveQuant[i];
                    coefficients[blockOffset + i] = (short)Math.Round(quantized, MidpointRounding.AwayFromZero);
                }
            }
        }

        return coefficients;
    }

    private static void WriteBitstream(
        Stream stream,
        JpegEncoderOptions options,
        int width,
        int height,
        bool grayscale,
        ComponentPlan[] components,
        short[][] quantizedComponents,
        ushort[] luminanceQuant,
        ushort[]? chrominanceQuant,
        int mcusAcross,
        int mcusDown)
    {
        JpegMarkerWriter.WriteMarkerOnly(stream, JpegMarker.Soi);

        Span<byte> jfif = stackalloc byte[14];
        "JFIF\0"u8.CopyTo(jfif);
        jfif[5] = 1;
        jfif[6] = 1;
        jfif[7] = 0;
        jfif[8] = 0;
        jfif[9] = 1;
        jfif[10] = 0;
        jfif[11] = 1;
        jfif[12] = 0;
        jfif[13] = 0;
        JpegMarkerWriter.WriteSegment(stream, JpegMarker.App0, jfif);

        WriteQuantTable(stream, 0, luminanceQuant);
        if (!grayscale)
        {
            WriteQuantTable(stream, 1, chrominanceQuant!);
        }

        var sofPayload = new byte[6 + (components.Length * 3)];
        sofPayload[0] = 8;
        sofPayload[1] = (byte)(height >> 8);
        sofPayload[2] = (byte)(height & 0xFF);
        sofPayload[3] = (byte)(width >> 8);
        sofPayload[4] = (byte)(width & 0xFF);
        sofPayload[5] = (byte)components.Length;
        for (int i = 0; i < components.Length; i++)
        {
            var c = components[i];
            int offset = 6 + (i * 3);
            sofPayload[offset] = c.Id;
            sofPayload[offset + 1] = (byte)((c.HSampling << 4) | c.VSampling);
            sofPayload[offset + 2] = c.QuantId;
        }

        JpegMarkerWriter.WriteSegment(stream, JpegMarker.Sof0, sofPayload);

        var luminanceDc = HuffmanEncodingTable.Build(StandardHuffmanTables.LuminanceDc.Counts, StandardHuffmanTables.LuminanceDc.Values);
        var luminanceAc = HuffmanEncodingTable.Build(StandardHuffmanTables.LuminanceAc.Counts, StandardHuffmanTables.LuminanceAc.Values);
        WriteHuffmanTable(stream, tableClass: 0, id: 0, luminanceDc);
        WriteHuffmanTable(stream, tableClass: 1, id: 0, luminanceAc);

        HuffmanEncodingTable? chrominanceDc = null;
        HuffmanEncodingTable? chrominanceAc = null;
        if (!grayscale)
        {
            chrominanceDc = HuffmanEncodingTable.Build(StandardHuffmanTables.ChrominanceDc.Counts, StandardHuffmanTables.ChrominanceDc.Values);
            chrominanceAc = HuffmanEncodingTable.Build(StandardHuffmanTables.ChrominanceAc.Counts, StandardHuffmanTables.ChrominanceAc.Values);
            WriteHuffmanTable(stream, tableClass: 0, id: 1, chrominanceDc);
            WriteHuffmanTable(stream, tableClass: 1, id: 1, chrominanceAc);
        }

        if (options.RestartInterval > 0)
        {
            Span<byte> dri = stackalloc byte[2];
            dri[0] = (byte)(options.RestartInterval >> 8);
            dri[1] = (byte)(options.RestartInterval & 0xFF);
            JpegMarkerWriter.WriteSegment(stream, JpegMarker.Dri, dri);
        }

        var sosPayload = new byte[1 + (components.Length * 2) + 3];
        sosPayload[0] = (byte)components.Length;
        for (int i = 0; i < components.Length; i++)
        {
            var c = components[i];
            int offset = 1 + (i * 2);
            sosPayload[offset] = c.Id;
            sosPayload[offset + 1] = (byte)((c.HuffTableId << 4) | c.HuffTableId);
        }

        sosPayload[^3] = 0;
        sosPayload[^2] = 63;
        sosPayload[^1] = 0;
        JpegMarkerWriter.WriteSegment(stream, JpegMarker.Sos, sosPayload);

        var dcTables = new HuffmanEncodingTable[components.Length];
        var acTables = new HuffmanEncodingTable[components.Length];
        dcTables[0] = luminanceDc;
        acTables[0] = luminanceAc;
        if (!grayscale)
        {
            dcTables[1] = dcTables[2] = chrominanceDc!;
            acTables[1] = acTables[2] = chrominanceAc!;
        }

        var writer = new JpegEntropyWriter(stream);
        var dcPredictors = new int[components.Length];
        int restartInterval = options.RestartInterval;
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
                    Array.Clear(dcPredictors);
                    restartIndex = (restartIndex + 1) & 7;
                    mcusUntilRestart = restartInterval;
                }

                for (int ci = 0; ci < components.Length; ci++)
                {
                    var plan = components[ci];
                    for (int by = 0; by < plan.VSampling; by++)
                    {
                        for (int bx = 0; bx < plan.HSampling; bx++)
                        {
                            int blockX = (mcuX * plan.HSampling) + bx;
                            int blockY = (mcuY * plan.VSampling) + by;
                            int blockOffset = ((blockY * plan.BlocksWide) + blockX) * 64;
                            var block = quantizedComponents[ci].AsSpan(blockOffset, 64);
                            EncodeBlock(writer, dcTables[ci], acTables[ci], block, ref dcPredictors[ci]);
                        }
                    }
                }

                if (restartInterval > 0)
                {
                    mcusUntilRestart--;
                }
            }
        }

        writer.Flush();
        JpegMarkerWriter.WriteMarkerOnly(stream, JpegMarker.Eoi);
    }

    private static void WriteQuantTable(Stream stream, byte id, ushort[] naturalOrderTable)
    {
        Span<byte> payload = stackalloc byte[65];
        payload[0] = id;
        for (int zigzagIndex = 0; zigzagIndex < 64; zigzagIndex++)
        {
            payload[1 + zigzagIndex] = (byte)naturalOrderTable[ZigZag.ToNaturalOrder[zigzagIndex]];
        }

        JpegMarkerWriter.WriteSegment(stream, JpegMarker.Dqt, payload);
    }

    private static void WriteHuffmanTable(Stream stream, int tableClass, int id, HuffmanEncodingTable table)
    {
        var payload = new byte[1 + 16 + table.Values.Length];
        payload[0] = (byte)((tableClass << 4) | id);
        table.Counts.CopyTo(payload.AsSpan(1));
        table.Values.CopyTo(payload.AsSpan(17));
        JpegMarkerWriter.WriteSegment(stream, JpegMarker.Dht, payload);
    }

    private static void EncodeBlock(JpegEntropyWriter writer, HuffmanEncodingTable dcTable, HuffmanEncodingTable acTable, ReadOnlySpan<short> naturalOrderCoefficients, ref int dcPredictor)
    {
        Span<short> zigzag = stackalloc short[64];
        for (int i = 0; i < 64; i++)
        {
            zigzag[i] = naturalOrderCoefficients[ZigZag.ToNaturalOrder[i]];
        }

        int dc = zigzag[0];
        int diff = dc - dcPredictor;
        dcPredictor = dc;

        int dcSize = MagnitudeBits(diff);
        dcTable.Encode(writer, (byte)dcSize);
        if (dcSize > 0)
        {
            writer.WriteBits(EncodeMagnitude(diff, dcSize), dcSize);
        }

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            short coefficient = zigzag[k];
            if (coefficient == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                acTable.Encode(writer, 0xF0); // ZRL: 16 zero coefficients
                run -= 16;
            }

            int acSize = MagnitudeBits(coefficient);
            acTable.Encode(writer, (byte)((run << 4) | acSize));
            writer.WriteBits(EncodeMagnitude(coefficient, acSize), acSize);
            run = 0;
        }

        if (run > 0)
        {
            acTable.Encode(writer, 0x00); // EOB
        }
    }

    private static int MagnitudeBits(int value)
    {
        value = Math.Abs(value);
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    private static int EncodeMagnitude(int value, int size) =>
        size == 0 ? 0 : value >= 0 ? value : value + (1 << size) - 1;

    private readonly record struct ComponentPlan(int Index, byte Id, byte HSampling, byte VSampling, byte QuantId, byte HuffTableId, int BlocksWide, int BlocksHigh);
}
