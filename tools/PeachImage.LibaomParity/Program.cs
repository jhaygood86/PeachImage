using System.Diagnostics;
using System.Globalization;
using System.Text;

using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Container;
using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.LibaomParity;

/// <summary>
/// Byte-exact comparison harness (project plan "compare-avif-encoding-to-lucky-clover.md", Phase 0): for one
/// input image, encodes it two ways -- via libaom's own <c>aomenc</c> CLI (the reference) and via
/// PeachImage's real lossless AVIF encoder (the actual) -- and reports the first byte at which they diverge,
/// both for the raw AV1 codestream and for the full muxed AVIF file. The AVIF container side of both files is
/// produced by the exact same <see cref="AvifContainerWriter.Write"/> call (see <see cref="BuildAvif"/>), so
/// any full-file divergence is guaranteed to trace back to the two AV1 codestreams -- the actual RD-search
/// question this project cares about -- not to container-level differences.
/// </summary>
internal static class Program
{
    private const string DefaultAomencPath = @"C:\Sources\GoogleSource\aom\build_ninja2\aomenc.exe";

    public static int Main(string[] args)
    {
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        string? inputPath = GetArg(args, "--input");
        string pattern = GetArg(args, "--pattern") ?? "solid";
        int width = int.Parse(GetArg(args, "--width") ?? "128", CultureInfo.InvariantCulture);
        int height = int.Parse(GetArg(args, "--height") ?? "128", CultureInfo.InvariantCulture);
        int effort = int.Parse(GetArg(args, "--effort") ?? "2", CultureInfo.InvariantCulture);
        string aomencPath = GetArg(args, "--aomenc") ?? DefaultAomencPath;
        string outDir = GetArg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "peachimage-libaom-parity");

        Directory.CreateDirectory(outDir);

        using var image = inputPath is not null
            ? Image.Load(inputPath, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgb24 })
            : GenerateSyntheticImage(pattern, width, height);

        Console.WriteLine($"Image: {image.Width}x{image.Height} ({(inputPath is not null ? $"loaded from {inputPath}" : $"synthetic '{pattern}'")}), effort={effort}");
        if (image.Width % 128 != 0 || image.Height % 128 != 0)
        {
            Console.WriteLine("NOTE: dimensions are not a multiple of 128 -- for lossless, PeachImage now signals the true (unpadded) frame_width/frame_height in the bitstream, matching aomenc's own partial-edge-superblock handling (see TileState.TrueMiCols's remarks), so sequence/frame header fields should match exactly. Any remaining divergence here is a partition/mode-search quality gap (like the palette/IntraBC gaps already tracked in the project plan), not a structural frame-size mismatch. Non-lossless still pads frame_width/frame_height to a superblock multiple and will show a real frame_size divergence.");
        }

        byte[] rgb = image.GetPixelSpan().ToArray();

        var (y, u, v) = Av1RgbToYuvIdentityConverter.Convert(rgb, image.Width, image.Height);
        string y4mPath = Path.Combine(outDir, "input.y4m");
        WriteY4m(y4mPath, y, u, v, image.Width, image.Height);

        string referenceObuPath = Path.Combine(outDir, "reference.obu");
        RunAomenc(aomencPath, y4mPath, referenceObuPath, effort);
        byte[] referenceObu = File.ReadAllBytes(referenceObuPath);

        // Captured live during the real encode (project plan Phase 1/Step 6's structural decision-log tool)
        // -- each record's EstimatedCost is PeachImage's own real-time RD-search cost estimate for that
        // exact leaf, data no amount of decoding either side's bitstream could ever recover (aomenc's own
        // internal cost estimates aren't observable without patching its C source, which this harness
        // deliberately avoids -- see ReportDecisionLogDiff's own remarks for why decoding is otherwise
        // sufficient for every other field here).
        var actualLeaves = new List<Av1BlockDecisionRecord>();
        var actualFrame = Av1FrameEncoder.Encode(rgb, image.Width, image.Height, monoChrome: false, quality: 75, lossless: true, effort, onLeafCommitted: actualLeaves.Add);
        byte[] actualObu = actualFrame.ObuBytes;

        int referenceSeqLevelIdx = Av1SequenceHeaderWriter.ComputeSeqLevelIdx(image.Width, image.Height);
        var referenceFrame = new Av1EncodedFrame(referenceObu, image.Width, image.Height, MonoChrome: false, Chroma444: true, referenceSeqLevelIdx);
        byte[] referenceAvif = BuildAvif(referenceFrame);
        byte[] actualAvif = BuildAvif(actualFrame);

        File.WriteAllBytes(Path.Combine(outDir, "reference.avif"), referenceAvif);
        File.WriteAllBytes(Path.Combine(outDir, "actual.avif"), actualAvif);
        File.WriteAllBytes(Path.Combine(outDir, "actual.obu"), actualObu);

        Console.WriteLine();
        Console.WriteLine("=== Raw AV1 OBU codestream ===");
        ReportObuStructure(referenceObu, "reference (aomenc)");
        ReportObuStructure(actualObu, "actual (PeachImage)");
        CompareAndReport(referenceObu, actualObu, isObu: true);

        Console.WriteLine();
        Console.WriteLine("=== Sequence header field diff ===");
        ReportSequenceHeaderDiff(referenceObu, actualObu);

        Console.WriteLine();
        Console.WriteLine("=== Frame header field diff ===");
        ReportFrameHeaderDiff(referenceObu, actualObu);

        Console.WriteLine();
        Console.WriteLine("=== Per-block decode summary (first few mi positions) ===");
        ReportBlockSummary(referenceObu, "reference");
        ReportBlockSummary(actualObu, "actual");

        Console.WriteLine();
        Console.WriteLine("=== Structural decision-log diff (project plan Phase 1/Step 6) ===");
        ReportDecisionLogDiff(referenceObu, actualLeaves);

        Console.WriteLine();
        Console.WriteLine("=== Full AVIF file (shared muxer -- divergence here should trace back to the OBU diff above) ===");
        CompareAndReport(referenceAvif, actualAvif, isObu: false);

        Console.WriteLine();
        Console.WriteLine($"Files written to: {outDir}");
    }

    /// <summary>Muxes an AV1-encoded frame into a full AVIF file via PeachImage's own (and only) container writer -- the same code path for both the reference and actual side, so container bytes are identical by construction.</summary>
    private static byte[] BuildAvif(Av1EncodedFrame frame)
    {
        using var stream = new MemoryStream();
        AvifContainerWriter.Write(stream, frame);
        return stream.ToArray();
    }

    private static Image GenerateSyntheticImage(string pattern, int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();

        switch (pattern)
        {
            case "solid":
                pixels.Fill(128);
                break;

            case "gradient":
                for (int yy = 0; yy < height; yy++)
                {
                    for (int xx = 0; xx < width; xx++)
                    {
                        int idx = ((yy * width) + xx) * 3;
                        pixels[idx] = (byte)(xx * 255 / Math.Max(1, width - 1));
                        pixels[idx + 1] = (byte)(yy * 255 / Math.Max(1, height - 1));
                        pixels[idx + 2] = 128;
                    }
                }

                break;

            case "checkerboard":
                for (int yy = 0; yy < height; yy++)
                {
                    for (int xx = 0; xx < width; xx++)
                    {
                        int idx = ((yy * width) + xx) * 3;
                        byte v = ((xx / 8) + (yy / 8)) % 2 == 0 ? (byte)255 : (byte)0;
                        pixels[idx] = v;
                        pixels[idx + 1] = v;
                        pixels[idx + 2] = v;
                    }
                }

                break;

            case "noise":
                GenerateNoiseImage(pixels, width, height);
                break;

            default:
                throw new ArgumentException($"Unknown synthetic pattern '{pattern}' (expected solid, gradient, checkerboard, or noise).");
        }

        return image;
    }

    /// <summary>Multi-octave value noise, smoothed via bilinear interpolation between coarse random grid points -- a cheap stand-in for photo-like texture (smooth low-frequency structure plus finer variation), with per-channel-correlated-but-not-identical values so chroma isn't trivially redundant with luma the way the checkerboard/gradient patterns deliberately are.</summary>
    private static void GenerateNoiseImage(Span<byte> pixels, int width, int height)
    {
        var random = new Random(12345);
        for (int channel = 0; channel < 3; channel++)
        {
            double[,] octave1 = GenerateGrid(random, (width / 16) + 2, (height / 16) + 2);
            double[,] octave2 = GenerateGrid(random, (width / 4) + 2, (height / 4) + 2);

            for (int yy = 0; yy < height; yy++)
            {
                for (int xx = 0; xx < width; xx++)
                {
                    double v1 = SampleGrid(octave1, xx / 16.0, yy / 16.0);
                    double v2 = SampleGrid(octave2, xx / 4.0, yy / 4.0);
                    double combined = (v1 * 0.7) + (v2 * 0.3);
                    int value = (int)Math.Round(Math.Clamp(combined * 255.0, 0, 255));

                    int idx = (((yy * width) + xx) * 3) + channel;
                    pixels[idx] = (byte)value;
                }
            }
        }
    }

    private static double[,] GenerateGrid(Random random, int cols, int rows)
    {
        var grid = new double[rows, cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                grid[r, c] = random.NextDouble();
            }
        }

        return grid;
    }

    private static double SampleGrid(double[,] grid, double x, double y)
    {
        int rows = grid.GetLength(0);
        int cols = grid.GetLength(1);
        int x0 = Math.Min((int)x, cols - 2);
        int y0 = Math.Min((int)y, rows - 2);
        double fx = x - x0;
        double fy = y - y0;

        double top = (grid[y0, x0] * (1 - fx)) + (grid[y0, x0 + 1] * fx);
        double bottom = (grid[y0 + 1, x0] * (1 - fx)) + (grid[y0 + 1, x0 + 1] * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }

    private static void WriteY4m(string path, int[] y, int[] u, int[] v, int width, int height)
    {
        using var stream = File.Create(path);
        byte[] header = Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F25:1 Ip A0:0 C444 XCOLORRANGE=FULL\nFRAME\n");
        stream.Write(header);
        WritePlane(stream, y, width, height);
        WritePlane(stream, u, width, height);
        WritePlane(stream, v, width, height);
    }

    private static void WritePlane(Stream stream, int[] plane, int width, int height)
    {
        var buffer = new byte[width * height];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)plane[i];
        }

        stream.Write(buffer);
    }

    private static void RunAomenc(string aomencPath, string y4mPath, string obuPath, int effort)
    {
        if (!File.Exists(aomencPath))
        {
            throw new FileNotFoundException($"aomenc.exe not found at '{aomencPath}'. Pass --aomenc <path>.", aomencPath);
        }

        var psi = new ProcessStartInfo(aomencPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(y4mPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(obuPath);
        psi.ArgumentList.Add("--obu");
        psi.ArgumentList.Add("--i444");
        psi.ArgumentList.Add("--lossless=1");
        psi.ArgumentList.Add("--matrix-coefficients=identity");
        // Match PeachImage's own fixed (cosmetic-only -- neither side's pixel math consults these)
        // color-primaries/transfer-characteristics choice (Av1SequenceHeaderWriter.ColorPrimaries/
        // TransferCharacteristics: BT.709/sRGB) rather than aomenc's own "unspecified" default, so this
        // doesn't show up as a spurious divergence unrelated to the actual RD-search comparison.
        psi.ArgumentList.Add("--color-primaries=bt709");
        psi.ArgumentList.Add("--transfer-characteristics=srgb");
        psi.ArgumentList.Add("--usage=2");
        psi.ArgumentList.Add("--limit=1");
        psi.ArgumentList.Add($"--cpu-used={effort}");
        psi.ArgumentList.Add("--tile-columns=0");
        psi.ArgumentList.Add("--tile-rows=0");
        psi.ArgumentList.Add("--sb-size=128");
        psi.ArgumentList.Add("--bit-depth=8");
        psi.ArgumentList.Add("--passes=1");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start aomenc.exe.");
        string stderr = process.StandardError.ReadToEnd();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"aomenc.exe exited with code {process.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private static void ReportObuStructure(byte[] data, string label)
    {
        try
        {
            var obus = Av1ObuReader.ReadObus(data, 0, data.Length);
            Console.WriteLine($"  {label}: {data.Length} bytes, " + string.Join(", ", obus.Select(o => $"{ObuTypeName(o.Type)}(payload={o.PayloadLength}B)")));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {label}: {data.Length} bytes, failed to parse as OBUs: {ex.Message}");
        }
    }

    private static string ObuTypeName(int type) => type switch
    {
        Av1ObuType.SequenceHeader => "SEQUENCE_HEADER",
        Av1ObuType.TemporalDelimiter => "TEMPORAL_DELIMITER",
        Av1ObuType.FrameHeader => "FRAME_HEADER",
        Av1ObuType.TileGroup => "TILE_GROUP",
        Av1ObuType.Frame => "FRAME",
        Av1ObuType.Metadata => "METADATA",
        Av1ObuType.RedundantFrameHeader => "REDUNDANT_FRAME_HEADER",
        Av1ObuType.TileList => "TILE_LIST",
        Av1ObuType.Padding => "PADDING",
        _ => $"UNKNOWN({type})",
    };

    private static void CompareAndReport(byte[] expected, byte[] actual, bool isObu)
    {
        int minLen = Math.Min(expected.Length, actual.Length);
        int firstDiff = -1;
        for (int i = 0; i < minLen; i++)
        {
            if (expected[i] != actual[i])
            {
                firstDiff = i;
                break;
            }
        }

        if (firstDiff == -1 && expected.Length == actual.Length)
        {
            Console.WriteLine($"BYTE-IDENTICAL ({expected.Length} bytes).");
            return;
        }

        if (firstDiff == -1)
        {
            firstDiff = minLen;
            Console.WriteLine($"Identical for the first {minLen} bytes, then lengths differ (reference={expected.Length}, actual={actual.Length}).");
        }
        else
        {
            Console.WriteLine($"First difference at byte offset {firstDiff} (of {expected.Length}/{actual.Length}): reference=0x{expected[firstDiff]:X2}, actual=0x{actual[firstDiff]:X2}");
        }

        int contextStart = Math.Max(0, firstDiff - 8);
        Console.WriteLine($"  reference: {HexRange(expected, contextStart, firstDiff, 24)}");
        Console.WriteLine($"  actual:    {HexRange(actual, contextStart, firstDiff, 24)}");

        if (isObu)
        {
            ReportDiffLocation(expected, firstDiff, "reference");
            ReportDiffLocation(actual, firstDiff, "actual");
        }
    }

    private static void ReportSequenceHeaderDiff(byte[] referenceObu, byte[] actualObu)
    {
        var referenceHeader = ParseSequenceHeader(referenceObu);
        var actualHeader = ParseSequenceHeader(actualObu);
        if (referenceHeader is null || actualHeader is null)
        {
            Console.WriteLine("  Could not locate a SEQUENCE_HEADER OBU in one or both streams.");
            return;
        }

        var fields = typeof(Av1SequenceHeader).GetProperties();
        bool anyDiff = false;
        foreach (var field in fields)
        {
            object? refValue = field.GetValue(referenceHeader);
            object? actualValue = field.GetValue(actualHeader);
            if (!Equals(refValue, actualValue))
            {
                anyDiff = true;
                Console.WriteLine($"  {field.Name}: reference={refValue}, actual={actualValue}");
            }
        }

        if (!anyDiff)
        {
            Console.WriteLine("  All parsed fields match.");
        }
    }

    private static void ReportFrameHeaderDiff(byte[] referenceObu, byte[] actualObu)
    {
        Av1FrameDecodeResult referenceResult;
        Av1FrameDecodeResult actualResult;
        try
        {
            referenceResult = Av1FrameDecoder.Decode(referenceObu);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not decode reference: {ex.Message}");
            return;
        }

        try
        {
            actualResult = Av1FrameDecoder.Decode(actualObu);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not decode actual: {ex.Message}");
            return;
        }

        DiffObject(referenceResult.Frame, actualResult.Frame, "Frame");
    }

    private static void DiffObject(object? reference, object? actual, string path)
    {
        if (Equals(reference, actual))
        {
            if (path == "Frame")
            {
                Console.WriteLine("  All parsed fields match.");
            }

            return;
        }

        if (reference is null || actual is null)
        {
            Console.WriteLine($"  {path}: reference={reference}, actual={actual}");
            return;
        }

        var type = reference.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
        {
            Console.WriteLine($"  {path}: reference={reference}, actual={actual}");
            return;
        }

        if (reference is System.Collections.IEnumerable refEnumerable && actual is System.Collections.IEnumerable actualEnumerable
            && type != typeof(string))
        {
            var refList = refEnumerable.Cast<object?>().ToList();
            var actualList = actualEnumerable.Cast<object?>().ToList();
            if (refList.Count != actualList.Count)
            {
                Console.WriteLine($"  {path}: length differs (reference={refList.Count}, actual={actualList.Count})");
                return;
            }

            for (int i = 0; i < refList.Count; i++)
            {
                DiffObject(refList[i], actualList[i], $"{path}[{i}]");
            }

            return;
        }

        foreach (var prop in type.GetProperties())
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? refValue = prop.GetValue(reference);
            object? actualValue = prop.GetValue(actual);
            if (!Equals(refValue, actualValue))
            {
                DiffObject(refValue, actualValue, $"{path}.{prop.Name}");
            }
        }
    }

    private static void ReportBlockSummary(byte[] data, string label)
    {
        try
        {
            var result = Av1FrameDecoder.Decode(data);
            int miCols = result.Frame.MiCols;
            int miRows = result.Frame.MiRows;
            var seen = new HashSet<(int MiSize, int YMode, bool Skip)>();
            var distinctBlocks = new List<(int R, int C, int MiSize, int YMode, bool Skip)>();
            for (int r = 0; r < miRows; r++)
            {
                for (int c = 0; c < miCols; c++)
                {
                    int idx = (r * miCols) + c;
                    var key = (result.MiSizes[idx], result.YModes[idx], result.Skips[idx]);
                    if (seen.Add(key))
                    {
                        distinctBlocks.Add((r, c, key.Item1, key.Item2, key.Item3));
                    }
                }
            }

            Console.WriteLine($"  {label}: {miCols}x{miRows} mi grid, {distinctBlocks.Count} distinct (miSize,yMode,skip) combos:");
            foreach (var block in distinctBlocks.Take(10))
            {
                Console.WriteLine($"    at (r={block.R},c={block.C}): miSize={block.MiSize}, yMode={block.YMode}, skip={block.Skip}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {label}: could not decode ({ex.Message})");
        }
    }

    /// <summary>
    /// Extracts one <see cref="Av1BlockDecisionRecord"/> per real leaf from a decoded frame's per-mi grids,
    /// in raster order by each leaf's own top-left position -- the project plan's Phase 1/Step 6 structural
    /// decision log, built purely by decoding (no libaom source patching needed: every field here except
    /// <see cref="Av1BlockDecisionRecord.EstimatedCost"/> is literally part of what the bitstream itself
    /// encodes, so this works identically for aomenc's own reference output and PeachImage's own). A leaf's
    /// entire footprint is redundantly written across every mi cell it covers (matching
    /// <see cref="Av1TileDecoder"/>'s own neighbor-context convention), so this walks the grid marking each
    /// footprint visited as it's consumed rather than assuming leaves start on any particular alignment.
    /// </summary>
    private static List<Av1BlockDecisionRecord> ExtractLeaves(Av1FrameDecodeResult result)
    {
        int miCols = result.Frame.MiCols;
        int miRows = result.Frame.MiRows;
        var visited = new bool[miCols * miRows];
        var leaves = new List<Av1BlockDecisionRecord>();

        for (int r = 0; r < miRows; r++)
        {
            for (int c = 0; c < miCols; c++)
            {
                int idx = (r * miCols) + c;
                if (visited[idx])
                {
                    continue;
                }

                int bSize = result.MiSizes[idx];
                int wMi = Av1BlockTables.Num4x4BlocksWide[bSize];
                int hMi = Av1BlockTables.Num4x4BlocksHigh[bSize];

                for (int dy = 0; dy < hMi && r + dy < miRows; dy++)
                {
                    int rowBase = (r + dy) * miCols;
                    for (int dx = 0; dx < wMi && c + dx < miCols; dx++)
                    {
                        visited[rowBase + c + dx] = true;
                    }
                }

                int paletteSizeY = result.PaletteSizesY[idx];
                int paletteSizeUV = result.PaletteSizesUV[idx];
                int colorBase = idx * 8;
                leaves.Add(new Av1BlockDecisionRecord
                {
                    R = r,
                    C = c,
                    WidthMi = wMi,
                    HeightMi = hMi,
                    YMode = result.YModes[idx],
                    AngleDeltaY = result.AngleDeltaYGrid[idx],
                    UvMode = result.UvModes[idx],
                    AngleDeltaUv = result.AngleDeltaUvGrid[idx],
                    Skip = result.Skips[idx],
                    PaletteSizeY = paletteSizeY,
                    PaletteSizeUV = paletteSizeUV,
                    PaletteColorsY = paletteSizeY > 0 ? string.Join(',', result.PaletteColorsYGrid.AsSpan(colorBase, paletteSizeY).ToArray()) : string.Empty,
                    PaletteColorsUV = paletteSizeUV > 0 ? string.Join(',', result.PaletteColorsUGrid.AsSpan(colorBase, paletteSizeUV).ToArray()) : string.Empty,
                    UsedIntrabc = result.IsInters[idx],
                    MvRow = result.MvRowsGrid[idx],
                    MvCol = result.MvColsGrid[idx],
                    UseFilterIntra = result.UseFilterIntraGrid[idx],
                    FilterIntraMode = result.FilterIntraModeGrid[idx],
                    EstimatedCost = null,
                });
            }
        }

        return leaves;
    }

    /// <summary>
    /// Diffs aomenc's own real, decoded decisions (ground truth -- reading <paramref name="referenceObu"/>'s
    /// own bitstream directly, no libaom source patching needed at all) against PeachImage's own live
    /// encode-time decisions (<paramref name="actualLeaves"/>, captured via <c>Av1FrameEncoder.Encode</c>'s
    /// <c>onLeafCommitted</c> hook -- see its own remarks for why this, not a second decode of PeachImage's
    /// own output, is used for the "actual" side: it's the only source that also carries
    /// <see cref="Av1BlockDecisionRecord.EstimatedCost"/>). Reports the *first* leaf where the two sides'
    /// logs disagree, and every differing field at that leaf (not just one) -- a real divergence often
    /// touches several related fields at once.
    /// </summary>
    private static void ReportDecisionLogDiff(byte[] referenceObu, List<Av1BlockDecisionRecord> actualLeaves)
    {
        List<Av1BlockDecisionRecord> referenceLeaves;
        try
        {
            referenceLeaves = ExtractLeaves(Av1FrameDecoder.Decode(referenceObu));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Could not decode reference for decision-log extraction: {ex.Message}");
            return;
        }

        // actualLeaves arrives in the encoder's own real recursive partition-commit order (raster over
        // superblocks, quad-recursive within, per-half-then-half for a Horz/Vert split) -- not the same
        // traversal order ExtractLeaves' own pure raster-by-top-left-position scan produces. Sorting by
        // position first makes the two lists directly, positionally comparable regardless of that difference
        // (a leaf's own top-left position is a unique, canonical identity for it either way).
        var sortedActual = actualLeaves.OrderBy(l => l.R).ThenBy(l => l.C).ToList();

        Console.WriteLine($"  {referenceLeaves.Count} real leaves (reference) vs. {sortedActual.Count} real leaves (actual).");

        int minCount = Math.Min(referenceLeaves.Count, sortedActual.Count);
        for (int i = 0; i < minCount; i++)
        {
            var reference = referenceLeaves[i];
            var actual = sortedActual[i];
            if (reference.R != actual.R || reference.C != actual.C || reference.WidthMi != actual.WidthMi || reference.HeightMi != actual.HeightMi)
            {
                Console.WriteLine($"  First structural divergence at leaf index {i}:");
                Console.WriteLine($"    reference: {reference}");
                Console.WriteLine($"    actual:    {actual}");
                Console.WriteLine("  (position/size itself differs here -- every leaf after this point in either log is misaligned with the other, so no further fields were compared.)");
                return;
            }

            var fieldDiffs = reference.DiffAgainst(actual).ToList();
            if (fieldDiffs.Count > 0)
            {
                Console.WriteLine($"  First structural divergence at leaf index {i} (r={reference.R}, c={reference.C}, size={reference.WidthMi}x{reference.HeightMi}):");
                foreach (string diff in fieldDiffs)
                {
                    Console.WriteLine($"    {diff}");
                }

                if (actual.EstimatedCost is { } cost)
                {
                    Console.WriteLine($"    (PeachImage's own real-time estimated cost for this leaf: {cost})");
                }

                Console.WriteLine($"    full reference: {reference}");
                Console.WriteLine($"    full actual:    {actual}");

                return;
            }
        }

        if (referenceLeaves.Count != sortedActual.Count)
        {
            Console.WriteLine($"  All {minCount} matched leaves are structurally identical, but leaf counts differ (reference={referenceLeaves.Count}, actual={sortedActual.Count}) -- one side has extra leaves beyond that point.");
            return;
        }

        Console.WriteLine($"  All {referenceLeaves.Count} leaves are structurally identical (position, size, y_mode, angle_delta, uv_mode, skip, palette, IntraBC, filter_intra).");
    }

    private static Av1SequenceHeader? ParseSequenceHeader(byte[] data)
    {
        var obus = Av1ObuReader.ReadObus(data, 0, data.Length);
        foreach (var obu in obus)
        {
            if (obu.Type == Av1ObuType.SequenceHeader)
            {
                var reader = new Av1BitReader(data, obu.PayloadOffset, obu.PayloadLength);
                return Av1SequenceHeader.Parse(reader);
            }
        }

        return null;
    }

    private static void ReportDiffLocation(byte[] data, int offset, string label)
    {
        try
        {
            var obus = Av1ObuReader.ReadObus(data, 0, data.Length);
            foreach (var obu in obus)
            {
                if (offset >= obu.PayloadOffset && offset < obu.PayloadOffset + obu.PayloadLength)
                {
                    Console.WriteLine($"  {label}: offset {offset} is inside {ObuTypeName(obu.Type)}'s payload (relative offset {offset - obu.PayloadOffset} of {obu.PayloadLength}).");
                    return;
                }
            }

            Console.WriteLine($"  {label}: offset {offset} is inside an OBU header/size field, not a payload.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {label}: could not localize offset {offset} ({ex.Message}).");
        }
    }

    private static string HexRange(byte[] data, int start, int markAt, int count)
    {
        var sb = new StringBuilder();
        int end = Math.Min(data.Length, start + count);
        for (int i = start; i < end; i++)
        {
            if (i == markAt)
            {
                sb.Append('[');
            }

            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            if (i == markAt)
            {
                sb.Append(']');
            }

            sb.Append(' ');
        }

        return sb.ToString();
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
