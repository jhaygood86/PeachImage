using System.Buffers;
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
/// <remarks>
/// Every intermediate plane/coefficient buffer is rented from <see cref="ArrayPool{T}"/> and returned once
/// the frame is fully written — the same rent/track/return-in-<c>finally</c> shape
/// <see cref="Decoding.FrameReconstructor"/> uses on the decode side. None of these buffers are ever read
/// past the exact width/height/block-count the caller tracks separately, so a pool-provided buffer larger
/// than requested is never a correctness hazard; and every buffer is fully overwritten before being read
/// (no progressive/partial fills like <see cref="Decoding.JpegCoefficientBuffer"/> needs to guard against),
/// so none of them need zeroing after renting.
/// </remarks>
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

        var rentedBytes = new List<byte[]>();
        var rentedShorts = new List<short[]>();

        try
        {
            byte[] yPlane;
            byte[]? cbPlane = null;
            byte[]? crPlane = null;

            if (grayscale)
            {
                yPlane = RentAndCopy(image.GetPixelSpan(), rentedBytes);
            }
            else
            {
                byte[] rgb = image.PixelFormat == PixelFormat.Rgba32 ? StripAlpha(image, rentedBytes) : RentAndCopy(image.GetPixelSpan(), rentedBytes);
                yPlane = Rent(width * height, rentedBytes);
                cbPlane = Rent(width * height, rentedBytes);
                crPlane = Rent(width * height, rentedBytes);
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

            var components = BuildComponentPlans(grayscale, chromaHRatio, chromaVRatio, mcusAcross, mcusDown, width, height, hMax, vMax);

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
                    sourcePlane = ChromaDownsampler.Downsample(sourcePlane, width, height, chromaHRatio, chromaVRatio, out sourceWidth, out sourceHeight, rentedBytes);
                }

                var padded = PadToBlockGrid(sourcePlane, sourceWidth, sourceHeight, plan.BlocksWide, plan.BlocksHigh, rentedBytes);
                quantizedComponents[i] = QuantizeComponent(padded, plan.BlocksWide, plan.BlocksHigh, plan.Index == 0 ? luminanceQuant : chrominanceQuant!, rentedShorts);
            }

            WriteBitstream(stream, options, width, height, grayscale, components, quantizedComponents, luminanceQuant, chrominanceQuant, mcusAcross, mcusDown);
        }
        finally
        {
            foreach (var buffer in rentedBytes)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            foreach (var buffer in rentedShorts)
            {
                ArrayPool<short>.Shared.Return(buffer);
            }
        }
    }

    private static byte[] Rent(int size, List<byte[]> rented)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        rented.Add(buffer);
        return buffer;
    }

    private static short[] RentShorts(int size, List<short[]> rented)
    {
        var buffer = ArrayPool<short>.Shared.Rent(size);
        rented.Add(buffer);
        return buffer;
    }

    private static byte[] RentAndCopy(ReadOnlySpan<byte> source, List<byte[]> rented)
    {
        var buffer = Rent(source.Length, rented);
        source.CopyTo(buffer);
        return buffer;
    }

    private static byte[] StripAlpha(Image image, List<byte[]> rented)
    {
        var source = image.GetPixelSpan();
        int pixelCount = image.Width * image.Height;
        var rgb = Rent(pixelCount * 3, rented);
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

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    // Actual (pre-MCU-padding) block extents per component, needed only by progressive encode's
    // non-interleaved AC scans, which must stop at a component's true block grid rather than walking
    // into the MCU-padding blocks baseline/DC-interleaved scans implicitly cover via BlocksWide/BlocksHigh.
    private static ComponentPlan[] BuildComponentPlans(bool grayscale, int chromaHRatio, int chromaVRatio, int mcusAcross, int mcusDown, int width, int height, int hMax, int vMax)
    {
        if (grayscale)
        {
            int blocksWide = CeilDiv(width, 8);
            int blocksHigh = CeilDiv(height, 8);
            return [new ComponentPlan(0, 1, 1, 1, 0, 0, mcusAcross, mcusDown, blocksWide, blocksHigh)];
        }

        int yBlocksWide = CeilDiv(width, 8);
        int yBlocksHigh = CeilDiv(height, 8);
        int chromaWidth = CeilDiv(width, hMax);
        int chromaHeight = CeilDiv(height, vMax);
        int cBlocksWide = CeilDiv(chromaWidth, 8);
        int cBlocksHigh = CeilDiv(chromaHeight, 8);

        return
        [
            new ComponentPlan(0, 1, (byte)chromaHRatio, (byte)chromaVRatio, 0, 0, mcusAcross * chromaHRatio, mcusDown * chromaVRatio, yBlocksWide, yBlocksHigh),
            new ComponentPlan(1, 2, 1, 1, 1, 1, mcusAcross, mcusDown, cBlocksWide, cBlocksHigh),
            new ComponentPlan(2, 3, 1, 1, 1, 1, mcusAcross, mcusDown, cBlocksWide, cBlocksHigh),
        ];
    }

    private static byte[] PadToBlockGrid(byte[] plane, int width, int height, int blocksWide, int blocksHigh, List<byte[]> rented)
    {
        int paddedWidth = blocksWide * 8;
        int paddedHeight = blocksHigh * 8;
        if (paddedWidth == width && paddedHeight == height)
        {
            return plane;
        }

        var padded = Rent(paddedWidth * paddedHeight, rented);
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

    // Larger than any legal quantized coefficient magnitude (|quantized| <= ~1448, since the AAN S(u)*S(v)
    // scale cancels between fdctOutput and effectiveQuant, leaving |trueDct[i] / quantTable[i]| with
    // quantTable[i] >= 1), so adding this bias makes the quotient unconditionally positive and a truncating
    // cast rounds to nearest — libjpeg-turbo's jcdctmgr.c trick, replacing a non-inlined Math.Round call on
    // every one of the 64 coefficients per block.
    private const double RoundingBias = 16384.5;
    private const int RoundingShift = 16384;

    private static short[] QuantizeComponent(byte[] padded, int blocksWide, int blocksHigh, ushort[] quantTable, List<short[]> rentedShorts)
    {
        int stride = blocksWide * 8;
        var coefficients = RentShorts(blocksWide * blocksHigh * 64, rentedShorts);
        Span<double> fdctOutput = stackalloc double[64];
        var effectiveQuant = DctKernelSelector.Forward.PrepareQuantTable(quantTable);

        // Precomputed once per component (like effectiveQuant itself), not per block: turns the 64
        // divisions every block used to pay into 64 multiplications, division being meaningfully more
        // expensive than multiplication for the same operation count.
        Span<double> reciprocalQuant = stackalloc double[64];
        for (int i = 0; i < 64; i++)
        {
            reciprocalQuant[i] = 1.0 / effectiveQuant[i];
        }

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int offset = (by * 8 * stride) + (bx * 8);
                DctKernelSelector.Forward.Transform(padded.AsSpan(offset), stride, fdctOutput);

                int blockOffset = ((by * blocksWide) + bx) * 64;
                for (int i = 0; i < 64; i++)
                {
                    double quantized = fdctOutput[i] * reciprocalQuant[i];
                    coefficients[blockOffset + i] = (short)((int)(quantized + RoundingBias) - RoundingShift);
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

        JpegMarkerWriter.WriteSegment(stream, options.Progressive ? JpegMarker.Sof2 : JpegMarker.Sof0, sofPayload);

        HuffmanEncodingTable luminanceDc, luminanceAc;
        HuffmanEncodingTable? chrominanceDc = null;
        HuffmanEncodingTable? chrominanceAc = null;

        if (options.OptimizeHuffmanTables && !options.Progressive)
        {
            var (lumaDcFreq, lumaAcFreq, chromaDcFreq, chromaAcFreq) =
                CountBaselineFrequencies(components, quantizedComponents, grayscale, options.RestartInterval, mcusAcross, mcusDown);

            luminanceDc = BuildOptimizedTable(lumaDcFreq);
            luminanceAc = BuildOptimizedTable(lumaAcFreq);
            WriteHuffmanTable(stream, tableClass: 0, id: 0, luminanceDc);
            WriteHuffmanTable(stream, tableClass: 1, id: 0, luminanceAc);

            if (!grayscale)
            {
                chrominanceDc = BuildOptimizedTable(chromaDcFreq!);
                chrominanceAc = BuildOptimizedTable(chromaAcFreq!);
                WriteHuffmanTable(stream, tableClass: 0, id: 1, chrominanceDc);
                WriteHuffmanTable(stream, tableClass: 1, id: 1, chrominanceAc);
            }
        }
        else
        {
            luminanceDc = HuffmanEncodingTable.Build(StandardHuffmanTables.LuminanceDc.Counts, StandardHuffmanTables.LuminanceDc.Values);
            luminanceAc = HuffmanEncodingTable.Build(StandardHuffmanTables.LuminanceAc.Counts, StandardHuffmanTables.LuminanceAc.Values);
            WriteHuffmanTable(stream, tableClass: 0, id: 0, luminanceDc);
            WriteHuffmanTable(stream, tableClass: 1, id: 0, luminanceAc);

            if (!grayscale)
            {
                chrominanceDc = HuffmanEncodingTable.Build(StandardHuffmanTables.ChrominanceDc.Counts, StandardHuffmanTables.ChrominanceDc.Values);
                chrominanceAc = HuffmanEncodingTable.Build(StandardHuffmanTables.ChrominanceAc.Counts, StandardHuffmanTables.ChrominanceAc.Values);
                WriteHuffmanTable(stream, tableClass: 0, id: 1, chrominanceDc);
                WriteHuffmanTable(stream, tableClass: 1, id: 1, chrominanceAc);
            }
        }

        if (options.RestartInterval > 0)
        {
            Span<byte> dri = stackalloc byte[2];
            dri[0] = (byte)(options.RestartInterval >> 8);
            dri[1] = (byte)(options.RestartInterval & 0xFF);
            JpegMarkerWriter.WriteSegment(stream, JpegMarker.Dri, dri);
        }

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
        if (options.Progressive)
        {
            WriteProgressiveScans(stream, writer, components, quantizedComponents, dcTables, options.RestartInterval, mcusAcross, mcusDown);
        }
        else
        {
            WriteBaselineScan(stream, writer, components, quantizedComponents, dcTables, acTables, options.RestartInterval, mcusAcross, mcusDown);
        }

        JpegMarkerWriter.WriteMarkerOnly(stream, JpegMarker.Eoi);
    }

    internal static void WriteScanHeader(Stream stream, ComponentPlan[] components, ReadOnlySpan<int> componentIndices, int ss, int se, int ah, int al, int? acTableIdOverride = null)
    {
        var sosPayload = new byte[1 + (componentIndices.Length * 2) + 3];
        sosPayload[0] = (byte)componentIndices.Length;
        for (int i = 0; i < componentIndices.Length; i++)
        {
            var c = components[componentIndices[i]];
            int offset = 1 + (i * 2);
            sosPayload[offset] = c.Id;

            // Non-interleaved progressive AC scans build and write their own table under a fixed id
            // (see ProgressiveScanEncoder) regardless of which component they're for, so the AC selector
            // nibble can't be derived from the component's own (baseline-oriented) HuffTableId here.
            int acTableId = acTableIdOverride ?? c.HuffTableId;
            sosPayload[offset + 1] = (byte)((c.HuffTableId << 4) | acTableId);
        }

        sosPayload[^3] = (byte)ss;
        sosPayload[^2] = (byte)se;
        sosPayload[^1] = (byte)((ah << 4) | al);
        JpegMarkerWriter.WriteSegment(stream, JpegMarker.Sos, sosPayload);
    }

    private static void WriteBaselineScan(
        Stream stream,
        JpegEntropyWriter writer,
        ComponentPlan[] components,
        short[][] quantizedComponents,
        HuffmanEncodingTable[] dcTables,
        HuffmanEncodingTable[] acTables,
        int restartInterval,
        int mcusAcross,
        int mcusDown)
    {
        Span<int> allIndices = stackalloc int[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            allIndices[i] = i;
        }

        WriteScanHeader(stream, components, allIndices, ss: 0, se: 63, ah: 0, al: 0);

        var dcEmitters = new Action<byte>[components.Length];
        var acEmitters = new Action<byte>[components.Length];
        for (int ci = 0; ci < components.Length; ci++)
        {
            var dcTable = dcTables[ci];
            var acTable = acTables[ci];
            dcEmitters[ci] = sym => dcTable.Encode(writer, sym);
            acEmitters[ci] = sym => acTable.Encode(writer, sym);
        }

        RunBaselineMcuLoop(stream, writer, components, quantizedComponents, dcEmitters, acEmitters, restartInterval, mcusAcross, mcusDown);
    }

    // Runs the exact MCU/block traversal (including restart-marker placement and dcPredictor resets) that
    // both the real write pass and CountBaselineFrequencies' throwaway counting pass need to share bit-for-bit
    // — factored out so the counting pass can never drift from the real pass's predictor-reset timing, which
    // would otherwise silently skew the DC diff symbols counted versus the ones actually encoded.
    private static void RunBaselineMcuLoop(
        Stream stream,
        JpegEntropyWriter writer,
        ComponentPlan[] components,
        short[][] quantizedComponents,
        Action<byte>[] dcEmitters,
        Action<byte>[] acEmitters,
        int restartInterval,
        int mcusAcross,
        int mcusDown)
    {
        var dcPredictors = new int[components.Length];
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
                            EncodeBlock(writer, dcEmitters[ci], acEmitters[ci], block, ref dcPredictors[ci]);
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
    }

    // First pass of the optimized-tables 2-pass approach: walks the same MCU loop the real write pass uses
    // (via RunBaselineMcuLoop, against a throwaway Stream.Null writer) counting DC/AC symbol frequencies
    // instead of emitting bits, so HuffmanTableOptimizer.Build can construct tables tuned to this image's
    // actual symbol distribution. Chrominance components 1 and 2 accumulate into one shared bucket each,
    // mirroring how the real pass's dcTables[1]==dcTables[2]/acTables[1]==acTables[2] serve both from one table.
    private static (int[] LumaDc, int[] LumaAc, int[]? ChromaDc, int[]? ChromaAc) CountBaselineFrequencies(
        ComponentPlan[] components,
        short[][] quantizedComponents,
        bool grayscale,
        int restartInterval,
        int mcusAcross,
        int mcusDown)
    {
        var lumaDc = new int[256];
        var lumaAc = new int[256];
        int[]? chromaDc = grayscale ? null : new int[256];
        int[]? chromaAc = grayscale ? null : new int[256];

        var dcEmitters = new Action<byte>[components.Length];
        var acEmitters = new Action<byte>[components.Length];
        dcEmitters[0] = sym => lumaDc[sym]++;
        acEmitters[0] = sym => lumaAc[sym]++;
        if (!grayscale)
        {
            dcEmitters[1] = dcEmitters[2] = sym => chromaDc![sym]++;
            acEmitters[1] = acEmitters[2] = sym => chromaAc![sym]++;
        }

        RunBaselineMcuLoop(Stream.Null, new JpegEntropyWriter(Stream.Null), components, quantizedComponents, dcEmitters, acEmitters, restartInterval, mcusAcross, mcusDown);

        return (lumaDc, lumaAc, chromaDc, chromaAc);
    }

    private static HuffmanEncodingTable BuildOptimizedTable(int[] frequencies)
    {
        var (counts, values) = HuffmanTableOptimizer.Build(frequencies);
        return HuffmanEncodingTable.Build(counts, values);
    }

    private static void WriteProgressiveScans(
        Stream stream,
        JpegEntropyWriter writer,
        ComponentPlan[] components,
        short[][] quantizedComponents,
        HuffmanEncodingTable[] dcTables,
        int restartInterval,
        int mcusAcross,
        int mcusDown)
    {
        foreach (var scan in ProgressiveScanScript.BuildDefaultScript(components.Length))
        {
            // AC scans write their own (scan-specific, optimized) DHT before their SOS — see
            // ProgressiveScanEncoder.EncodeScan — so the SOS write is not hoisted out here uniformly.
            ProgressiveScanEncoder.EncodeScan(writer, stream, scan, components, quantizedComponents, dcTables, restartInterval, mcusAcross, mcusDown);
            writer.Flush();
        }
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

    internal static void WriteHuffmanTable(Stream stream, int tableClass, int id, HuffmanEncodingTable table)
    {
        var payload = new byte[1 + 16 + table.Values.Length];
        payload[0] = (byte)((tableClass << 4) | id);
        table.Counts.CopyTo(payload.AsSpan(1));
        table.Values.CopyTo(payload.AsSpan(17));
        JpegMarkerWriter.WriteSegment(stream, JpegMarker.Dht, payload);
    }

    private static void EncodeBlock(JpegEntropyWriter writer, Action<byte> emitDc, Action<byte> emitAc, ReadOnlySpan<short> naturalOrderCoefficients, ref int dcPredictor)
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
        emitDc((byte)dcSize);
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
                emitAc(0xF0); // ZRL: 16 zero coefficients
                run -= 16;
            }

            int acSize = MagnitudeBits(coefficient);
            emitAc((byte)((run << 4) | acSize));
            writer.WriteBits(EncodeMagnitude(coefficient, acSize), acSize);
            run = 0;
        }

        if (run > 0)
        {
            emitAc(0x00); // EOB
        }
    }

    internal static int MagnitudeBits(int value)
    {
        uint magnitude = (uint)Math.Abs(value);
        return magnitude == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(magnitude);
    }

    internal static int EncodeMagnitude(int value, int size) =>
        size == 0 ? 0 : value >= 0 ? value : value + (1 << size) - 1;

    internal readonly record struct ComponentPlan(int Index, byte Id, byte HSampling, byte VSampling, byte QuantId, byte HuffTableId, int BlocksWide, int BlocksHigh, int ActualBlocksWide, int ActualBlocksHigh);
}
