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
        // --corpus <dir>: Phase 0's own corpus-iterating mode (project plan
        // "for-the-attached-image-nifty-kahan.md", Phase 0 deliverable #2) -- runs every *.avif file in
        // <dir> through this same reference-vs-actual byte comparison, instead of the single hand-picked
        // image the rest of this tool targets. Takes over the whole run; every other single-image flag
        // below is ignored in this mode except --effort/--aomenc/--out.
        string? corpusDir = GetArg(args, "--corpus");
        if (corpusDir is not null)
        {
            RunCorpus(args, corpusDir);
            return;
        }

        // --diagnose <dir>: root-causing helper for a round-trip pixel mismatch the corpus mode already
        // flagged -- reads that file's own cached input.y4m/reference.obu/actual.obu (already written by a
        // prior --corpus run) and compares the ORIGINAL source pixels against both sides' own decoded
        // output, and the two decoded outputs against each other, to localize which side (or both) actually
        // has the bug rather than just knowing they disagree.
        string? diagnoseDir = GetArg(args, "--diagnose");
        if (diagnoseDir is not null)
        {
            RunDiagnose(diagnoseDir);
            return;
        }

        string? inputPath = GetArg(args, "--input");
        string pattern = GetArg(args, "--pattern") ?? "solid";
        int width = int.Parse(GetArg(args, "--width") ?? "128", CultureInfo.InvariantCulture);
        int height = int.Parse(GetArg(args, "--height") ?? "128", CultureInfo.InvariantCulture);
        int effort = int.Parse(GetArg(args, "--effort") ?? "2", CultureInfo.InvariantCulture);
        string aomencPath = GetArg(args, "--aomenc") ?? DefaultAomencPath;
        string outDir = GetArg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "peachimage-libaom-parity");

        // --reuse-cache <dir>: skip both image loading/generation AND the (slow, external-process) aomenc
        // invocation entirely, reusing a prior run's own input.y4m + reference.obu pair verbatim -- input.y4m
        // is already exactly the pixel data any earlier --input/--pattern run produced (Av1RgbToYuvIdentityConverter's
        // own Y=G/U=B/V=R mapping is trivially invertible, see its own remarks), and reference.obu is aomenc's
        // own deterministic output for that same pixel data at the same effort, so re-deriving both from
        // scratch would just reproduce byte-identical files at the cost of a real aomenc re-run (and, for a
        // network-sourced real test image with no locally-cached original, might not even be reproducible at
        // all without re-downloading it). Effort/aomenc/pattern/width/height/input args are ignored when this
        // is set.
        string? reuseCacheDir = GetArg(args, "--reuse-cache");

        Directory.CreateDirectory(outDir);

        byte[] rgb;
        int imageWidth;
        int imageHeight;
        string y4mPath;
        string referenceObuPath;
        byte[] referenceObu;

        if (reuseCacheDir is not null)
        {
            string cachedY4mPath = Path.Combine(reuseCacheDir, "input.y4m");
            string cachedReferenceObuPath = Path.Combine(reuseCacheDir, "reference.obu");
            (int[] cy, int[] cu, int[] cv, imageWidth, imageHeight) = ReadY4m(cachedY4mPath);
            rgb = new byte[imageWidth * imageHeight * 3];
            for (int i = 0; i < imageWidth * imageHeight; i++)
            {
                // Inverse of Av1RgbToYuvIdentityConverter.Convert: R = V, G = Y, B = U.
                rgb[(i * 3) + 0] = (byte)cv[i];
                rgb[(i * 3) + 1] = (byte)cy[i];
                rgb[(i * 3) + 2] = (byte)cu[i];
            }

            y4mPath = Path.Combine(outDir, "input.y4m");
            referenceObuPath = Path.Combine(outDir, "reference.obu");
            File.Copy(cachedY4mPath, y4mPath, overwrite: true);
            File.Copy(cachedReferenceObuPath, referenceObuPath, overwrite: true);
            referenceObu = File.ReadAllBytes(referenceObuPath);

            Console.WriteLine($"Image: {imageWidth}x{imageHeight} (reused cache from {reuseCacheDir}), effort={effort}");
        }
        else
        {
            using var image = inputPath is not null
                ? Image.Load(inputPath, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgb24 })
                : GenerateSyntheticImage(pattern, width, height);

            imageWidth = image.Width;
            imageHeight = image.Height;

            Console.WriteLine($"Image: {image.Width}x{image.Height} ({(inputPath is not null ? $"loaded from {inputPath}" : $"synthetic '{pattern}'")}), effort={effort}");
            if (image.Width % 128 != 0 || image.Height % 128 != 0)
            {
                Console.WriteLine("NOTE: dimensions are not a multiple of 128 -- for lossless, PeachImage now signals the true (unpadded) frame_width/frame_height in the bitstream, matching aomenc's own partial-edge-superblock handling (see TileState.TrueMiCols's remarks), so sequence/frame header fields should match exactly. Any remaining divergence here is a partition/mode-search quality gap (like the palette/IntraBC gaps already tracked in the project plan), not a structural frame-size mismatch. Non-lossless still pads frame_width/frame_height to a superblock multiple and will show a real frame_size divergence.");
            }

            rgb = image.GetPixelSpan().ToArray();

            var (y, u, v) = Av1RgbToYuvIdentityConverter.Convert(rgb, image.Width, image.Height);
            y4mPath = Path.Combine(outDir, "input.y4m");
            WriteY4m(y4mPath, y, u, v, image.Width, image.Height);

            referenceObuPath = Path.Combine(outDir, "reference.obu");
            RunAomenc(aomencPath, y4mPath, referenceObuPath, effort);
            referenceObu = File.ReadAllBytes(referenceObuPath);
        }

        // Captured live during the real encode (project plan Phase 1/Step 6's structural decision-log tool)
        // -- each record's EstimatedCost is PeachImage's own real-time RD-search cost estimate for that
        // exact leaf, data no amount of decoding either side's bitstream could ever recover (aomenc's own
        // internal cost estimates aren't observable without patching its C source, which this harness
        // deliberately avoids -- see ReportDecisionLogDiff's own remarks for why decoding is otherwise
        // sufficient for every other field here).
        var actualLeaves = new List<Av1BlockDecisionRecord>();
        var actualFrame = Av1FrameEncoder.Encode(rgb, imageWidth, imageHeight, monoChrome: false, quality: 75, lossless: true, effort, onLeafCommitted: actualLeaves.Add);
        byte[] actualObu = actualFrame.ObuBytes;

        int referenceSeqLevelIdx = Av1SequenceHeaderWriter.ComputeSeqLevelIdx(imageWidth, imageHeight);
        var referenceFrame = new Av1EncodedFrame(referenceObu, imageWidth, imageHeight, MonoChrome: false, Chroma444: true, referenceSeqLevelIdx);
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

        // Self round-trip structural check (round N+10's own investigation): actualLeaves is the encoder's
        // own real-time INTENT (captured live via onLeafCommitted during the real commit pass); decoding
        // PeachImage's own actualObu independently reveals what a real decoder actually reads back for that
        // same bitstream. These two should always be identical -- any difference here is a genuine
        // encoder/decoder desync (wrong bits written, or the same bits read under a different, already-
        // diverged CDF/context state), never a quality/byte-count question, and pinpoints the earliest
        // structurally-divergent node far more directly than hunting for the first wrong pixel.
        Console.WriteLine();
        Console.WriteLine("=== Self round-trip structural check (encoder intent vs. decode of its own output) ===");
        ReportDecisionLogDiff(actualObu, actualLeaves);

        string? cdfSlotFilter = Environment.GetEnvironmentVariable("PEACHIMAGE_DUMP_UVMODE_LEAVES");
        if (cdfSlotFilter is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Every leaf touching UvModeCflAllowed[{cdfSlotFilter}] (encoder intent, sorted by position, not real commit order) ===");
            int targetYMode = int.Parse(cdfSlotFilter, CultureInfo.InvariantCulture);
            int idx = 0;
            foreach (var leaf in actualLeaves.OrderBy(l => l.R).ThenBy(l => l.C))
            {
                bool cflAllowed = leaf.WidthMi == 1 && leaf.HeightMi == 1;
                if (cflAllowed && leaf.YMode == targetYMode)
                {
                    Console.WriteLine($"  leaf {idx}: {leaf}");
                }

                idx++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Full AVIF file (shared muxer -- divergence here should trace back to the OBU diff above) ===");
        CompareAndReport(referenceAvif, actualAvif, isObu: false);

        Console.WriteLine();
        Console.WriteLine($"Files written to: {outDir}");
    }

    /// <summary>
    /// Phase 0's corpus-iterating mode: decodes every corpus <c>.avif</c> file this decoder can handle in a
    /// directly re-encodable pixel format (Rgb24 only for now -- see the skip branch below), re-encodes each
    /// via both real <c>aomenc</c> and PeachImage's own lossless encoder at matching settings, and reports
    /// byte-identical pass/fail per file plus an aggregate summary. Deliberately does NOT fail the process
    /// (exit code stays 0) on a mismatch -- unlike the single-image mode's exhaustive per-field diff, this is
    /// a *measurement* tool for tracking the corpus-wide gap across many real images at once, not (yet) the
    /// standing, must-pass regression test the project plan's own definition of done ultimately calls for;
    /// turning this into a hard-failing <c>dotnet test</c> case is deferred until the gap has actually closed
    /// enough for that to be meaningful rather than permanently red.
    /// </summary>
    private static void RunCorpus(string[] args, string corpusDir)
    {
        int effort = int.Parse(GetArg(args, "--effort") ?? "2", CultureInfo.InvariantCulture);
        string aomencPath = GetArg(args, "--aomenc") ?? DefaultAomencPath;
        string outDir = GetArg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "peachimage-libaom-parity-corpus");
        Directory.CreateDirectory(outDir);

        string[] files = Directory.GetFiles(corpusDir, "*.avif").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"Corpus: {corpusDir} ({files.Length} .avif files), effort={effort}");
        Console.WriteLine();

        int skipped = 0;
        int identical = 0;
        int mismatched = 0;
        int pixelIdentical = 0;
        int pixelMismatched = 0;
        long totalReferenceBytes = 0;
        long totalActualBytes = 0;
        var mismatches = new List<string>();
        var pixelMismatches = new List<string>();

        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            Image? image;
            try
            {
                using var stream = File.OpenRead(file);
                image = AvifDecoder.Decode(stream);
            }
            catch (AvifDecodingException ex)
            {
                Console.WriteLine($"  SKIP {name}: decode error ({ex.Message})");
                skipped++;
                continue;
            }
            catch (AvifUnsupportedFeatureException ex)
            {
                Console.WriteLine($"  SKIP {name}: unsupported feature ({ex.Message})");
                skipped++;
                continue;
            }

            using (image)
            {
                // Rgb24-only for now, matching AvifFfmpegReferenceTests' own comparable-pixel-format scope
                // decision: Gray8 would need Av1FrameEncoder.Encode's own monoChrome path wired up here (not
                // yet done in this harness), and higher-bit-depth/alpha formats aren't a pixel shape this
                // encoder round-trips through Av1RgbToYuvIdentityConverter at all.
                if (image.PixelFormat != PixelFormat.Rgb24)
                {
                    Console.WriteLine($"  SKIP {name}: pixel format {image.PixelFormat} out of this harness's current scope (Rgb24 only)");
                    skipped++;
                    continue;
                }

                if (image.Width <= 0 || image.Height <= 0)
                {
                    Console.WriteLine($"  SKIP {name}: degenerate size {image.Width}x{image.Height}");
                    skipped++;
                    continue;
                }

                byte[] rgb = image.GetPixelSpan().ToArray();
                string imageOutDir = Path.Combine(outDir, name);
                Directory.CreateDirectory(imageOutDir);

                var (y, u, v) = Av1RgbToYuvIdentityConverter.Convert(rgb, image.Width, image.Height);
                string y4mPath = Path.Combine(imageOutDir, "input.y4m");
                WriteY4m(y4mPath, y, u, v, image.Width, image.Height);

                string referenceObuPath = Path.Combine(imageOutDir, "reference.obu");
                byte[] referenceObu;
                try
                {
                    RunAomenc(aomencPath, y4mPath, referenceObuPath, effort);
                    referenceObu = File.ReadAllBytes(referenceObuPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  SKIP {name}: aomenc failed ({ex.Message})");
                    skipped++;
                    continue;
                }

                byte[] actualObu;
                Av1EncodedFrame actualFrame;
                try
                {
                    actualFrame = Av1FrameEncoder.Encode(rgb, image.Width, image.Height, monoChrome: false, quality: 75, lossless: true, effort);
                    actualObu = actualFrame.ObuBytes;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  FAIL {name}: PeachImage encode threw {ex.GetType().Name}: {ex.Message}");
                    mismatched++;
                    mismatches.Add(name);
                    continue;
                }

                File.WriteAllBytes(Path.Combine(imageOutDir, "actual.obu"), actualObu);

                totalReferenceBytes += referenceObu.Length;
                totalActualBytes += actualObu.Length;

                // Round-trip pixel check (project plan definition-of-done item 4, escalation-4's own
                // "roundtrip through both libaom and PeachImage with identical pixels and bytes" directive):
                // independently mandatory alongside the byte-identical check above, not implied by it -- this
                // is the check that actually matters to a real consumer of the format, and it catches a bug in
                // the byte-comparison harness itself that a pure byte diff couldn't. Decodes both sides' own
                // encoded output back through PeachImage's own (and only) AVIF decoder -- the same container
                // each side's own AVIF file was built with via BuildAvif, so this exercises the real decode
                // path a real consumer would use, not a special test-only reader.
                //
                // Checks TWO independent things, not one: (a) actual's own real round-trip against the TRUE
                // source pixels (`rgb`, already decoded from the corpus file itself) -- this is the only check
                // that can actually indict PeachImage's own encoder, and (b) reference vs actual, which is a
                // separate, weaker signal (aomenc's own reference doesn't always round-trip to source
                // perfectly either for every corpus file -- confirmed directly for at least one grid-tiled
                // fixture, `sofa_grid1x5_420`, where aomenc's own decoded output disagreed with source while
                // PeachImage's own matched exactly -- so a reference-vs-actual mismatch alone does NOT mean
                // PeachImage has a bug, only that the two sides differ, which byte-count differences already
                // told us). (a) is what actually gates correctness; (b) is kept only as an extra diagnostic.
                string pixelNote;
                bool pixelsMatch;
                try
                {
                    int seqLevelIdx = Av1SequenceHeaderWriter.ComputeSeqLevelIdx(image.Width, image.Height);
                    var referenceFrame = new Av1EncodedFrame(referenceObu, image.Width, image.Height, MonoChrome: false, Chroma444: true, seqLevelIdx);
                    byte[] referenceAvif = BuildAvif(referenceFrame);
                    byte[] actualAvif = BuildAvif(actualFrame);

                    using var referenceStream = new MemoryStream(referenceAvif);
                    using var actualStream = new MemoryStream(actualAvif);
                    using var referenceDecoded = AvifDecoder.Decode(referenceStream);
                    using var actualDecoded = AvifDecoder.Decode(actualStream);

                    bool actualMatchesSource = actualDecoded.Width == image.Width && actualDecoded.Height == image.Height
                        && actualDecoded.PixelFormat == PixelFormat.Rgb24 && actualDecoded.GetPixelSpan().SequenceEqual(rgb);

                    bool referenceMatchesActual = referenceDecoded.Width == actualDecoded.Width && referenceDecoded.Height == actualDecoded.Height
                        && referenceDecoded.PixelFormat == actualDecoded.PixelFormat && referenceDecoded.GetPixelSpan().SequenceEqual(actualDecoded.GetPixelSpan());

                    pixelsMatch = actualMatchesSource;
                    pixelNote = actualMatchesSource
                        ? (referenceMatchesActual ? "pixels match (actual==source, reference==actual)" : "pixels match (actual==source, reference DIFFERS from actual/source -- aomenc's own reference didn't round-trip, not a PeachImage bug)")
                        : (referenceMatchesActual ? "PIXELS WRONG (actual!=source, but reference==actual -- aomenc has the same bug, or both share a decode issue)" : "PIXELS WRONG (actual!=source)");
                }
                catch (Exception ex)
                {
                    pixelsMatch = false;
                    pixelNote = $"round-trip decode threw {ex.GetType().Name}: {ex.Message}";
                }

                if (pixelsMatch)
                {
                    pixelIdentical++;
                }
                else
                {
                    pixelMismatched++;
                    pixelMismatches.Add(name);
                }

                if (referenceObu.AsSpan().SequenceEqual(actualObu))
                {
                    Console.WriteLine($"  OK   {name}: {image.Width}x{image.Height}, {referenceObu.Length} bytes, byte-identical, {pixelNote}");
                    identical++;
                }
                else
                {
                    double gapPct = ((double)actualObu.Length - referenceObu.Length) / referenceObu.Length * 100.0;
                    Console.WriteLine($"  DIFF {name}: {image.Width}x{image.Height}, reference={referenceObu.Length}B actual={actualObu.Length}B ({gapPct:+0.00;-0.00}%), {pixelNote}");
                    mismatched++;
                    mismatches.Add(name);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Corpus summary ===");
        Console.WriteLine($"Total files: {files.Length}");
        Console.WriteLine($"Skipped (out of this harness's current scope): {skipped}");
        Console.WriteLine($"Compared: {identical + mismatched} (byte-identical: {identical}, mismatched: {mismatched})");
        Console.WriteLine($"Round-trip pixels: {pixelIdentical} match, {pixelMismatched} differ (independent of the byte check above -- see the plan's own definition-of-done item 4)");
        if (totalReferenceBytes > 0)
        {
            double overallGapPct = ((double)totalActualBytes - totalReferenceBytes) / totalReferenceBytes * 100.0;
            Console.WriteLine($"Aggregate bytes: reference={totalReferenceBytes} actual={totalActualBytes} ({overallGapPct:+0.00;-0.00}%)");
        }

        if (mismatches.Count > 0)
        {
            Console.WriteLine($"Mismatched files ({mismatches.Count}): {string.Join(", ", mismatches)}");
        }

        if (pixelMismatches.Count > 0)
        {
            Console.WriteLine($"Pixel-mismatched files ({pixelMismatches.Count}): {string.Join(", ", pixelMismatches)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Per-file artifacts written under: {outDir}");
    }

    private static void RunDiagnose(string dir)
    {
        var (y, u, v, width, height) = ReadY4m(Path.Combine(dir, "input.y4m"));
        byte[] originalRgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            // Inverse of Av1RgbToYuvIdentityConverter.Convert: R = V, G = Y, B = U.
            originalRgb[(i * 3) + 0] = (byte)v[i];
            originalRgb[(i * 3) + 1] = (byte)y[i];
            originalRgb[(i * 3) + 2] = (byte)u[i];
        }

        byte[] referenceObu = File.ReadAllBytes(Path.Combine(dir, "reference.obu"));
        byte[] actualObu = File.ReadAllBytes(Path.Combine(dir, "actual.obu"));

        int seqLevelIdx = Av1SequenceHeaderWriter.ComputeSeqLevelIdx(width, height);
        var referenceFrame = new Av1EncodedFrame(referenceObu, width, height, MonoChrome: false, Chroma444: true, seqLevelIdx);
        var actualFrame = new Av1EncodedFrame(actualObu, width, height, MonoChrome: false, Chroma444: true, seqLevelIdx);

        byte[] referenceAvif = BuildAvif(referenceFrame);
        byte[] actualAvif = BuildAvif(actualFrame);

        using var refImg = AvifDecoder.Decode(new MemoryStream(referenceAvif));
        using var actImg = AvifDecoder.Decode(new MemoryStream(actualAvif));

        Console.WriteLine($"original:  {width}x{height}");
        Console.WriteLine($"reference: {refImg.Width}x{refImg.Height} {refImg.PixelFormat}");
        Console.WriteLine($"actual:    {actImg.Width}x{actImg.Height} {actImg.PixelFormat}");
        Console.WriteLine();

        CompareRgb("original  vs reference", originalRgb, refImg.GetPixelSpan().ToArray(), width);
        CompareRgb("original  vs actual   ", originalRgb, actImg.GetPixelSpan().ToArray(), width);
        var (px, py) = CompareRgb("reference vs actual   ", refImg.GetPixelSpan().ToArray(), actImg.GetPixelSpan().ToArray(), width);

        if (px >= 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Per-mi metadata around first pixel-diverging position (pixel x={px},y={py} -> mi r={py / 4},c={px / 4}) ===");
            ReportMiNeighborhood(referenceObu, "reference", px / 4, py / 4);
            ReportMiNeighborhood(actualObu, "actual", px / 4, py / 4);
        }

        string? atArg = Environment.GetEnvironmentVariable("PEACHIMAGE_DIAGNOSE_AT");
        if (atArg is not null)
        {
            string[] parts = atArg.Split(',');
            int atX = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int atY = int.Parse(parts[1], CultureInfo.InvariantCulture);
            Console.WriteLine();
            Console.WriteLine($"=== Raw 4x4 RGB dump at (x={atX},y={atY}) ===");
            DumpRgbRegion("original ", originalRgb, width, atX, atY);
            DumpRgbRegion("reference", refImg.GetPixelSpan().ToArray(), width, atX, atY);
            DumpRgbRegion("actual   ", actImg.GetPixelSpan().ToArray(), width, atX, atY);
        }
    }

    private static void DumpRgbRegion(string label, byte[] rgb, int width, int atX, int atY)
    {
        for (int dy = 0; dy < 4; dy++)
        {
            var row = new List<string>();
            for (int dx = 0; dx < 4; dx++)
            {
                int idx = (((atY + dy) * width) + atX + dx) * 3;
                row.Add($"({rgb[idx]},{rgb[idx + 1]},{rgb[idx + 2]})");
            }

            Console.WriteLine($"  {label} y={atY + dy}: {string.Join(" ", row)}");
        }
    }

    private static void ReportMiNeighborhood(byte[] obu, string label, int centerC, int centerR)
    {
        try
        {
            var result = Av1FrameDecoder.Decode(obu);
            int miCols = result.Frame.MiCols;
            int miRows = result.Frame.MiRows;

            for (int r = Math.Max(0, centerR - 2); r <= Math.Min(miRows - 1, centerR + 2); r++)
            {
                for (int c = Math.Max(0, centerC - 2); c <= Math.Min(miCols - 1, centerC + 2); c++)
                {
                    int idx = (r * miCols) + c;
                    Console.WriteLine($"  {label} (r={r},c={c}): miSize={result.MiSizes[idx]}, yMode={result.YModes[idx]}, uvMode={result.UvModes[idx]}, skip={result.Skips[idx]}, paletteY={result.PaletteSizesY[idx]}, paletteUV={result.PaletteSizesUV[idx]}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {label}: could not decode ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static (int X, int Y) CompareRgb(string label, byte[] a, byte[] b, int width)
    {
        int len = Math.Min(a.Length, b.Length);
        int diffCount = 0;
        int firstDiffIdx = -1;
        for (int i = 0; i < len; i++)
        {
            if (a[i] != b[i])
            {
                diffCount++;
                if (firstDiffIdx < 0)
                {
                    firstDiffIdx = i;
                }
            }
        }

        if (diffCount == 0)
        {
            Console.WriteLine($"  {label}: identical ({len} bytes)");
            return (-1, -1);
        }

        int pixelIdx = firstDiffIdx / 3;
        int px = pixelIdx % width;
        int py = pixelIdx / width;
        Console.WriteLine($"  {label}: {diffCount}/{len} bytes differ ({100.0 * diffCount / len:F2}%), first at byte {firstDiffIdx} (pixel x={px},y={py},channel={firstDiffIdx % 3}): a={a[firstDiffIdx]} b={b[firstDiffIdx]}");
        return (px, py);
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

    /// <summary>The exact inverse of <see cref="WriteY4m"/>, for the same fixed C444/8-bit shape it always
    /// writes (single "FRAME" header, no subsampling, no per-sample bit depth beyond 8) -- not a general Y4M
    /// parser.</summary>
    private static (int[] Y, int[] U, int[] V, int Width, int Height) ReadY4m(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int headerEnd = Array.IndexOf(bytes, (byte)'\n');
        string headerLine = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        int frameLineEnd = Array.IndexOf(bytes, (byte)'\n', headerEnd + 1);

        int width = 0;
        int height = 0;
        foreach (string token in headerLine.Split(' '))
        {
            if (token.Length > 1 && token[0] == 'W')
            {
                width = int.Parse(token.AsSpan(1), CultureInfo.InvariantCulture);
            }
            else if (token.Length > 1 && token[0] == 'H')
            {
                height = int.Parse(token.AsSpan(1), CultureInfo.InvariantCulture);
            }
        }

        int planeSize = width * height;
        int offset = frameLineEnd + 1;
        var y = ReadPlane(bytes, offset, planeSize);
        var u = ReadPlane(bytes, offset + planeSize, planeSize);
        var v = ReadPlane(bytes, offset + (2 * planeSize), planeSize);
        return (y, u, v, width, height);
    }

    private static int[] ReadPlane(byte[] bytes, int offset, int count)
    {
        var plane = new int[count];
        for (int i = 0; i < count; i++)
        {
            plane[i] = bytes[offset + i];
        }

        return plane;
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

        // Reports up to maxDivergences, rather than stopping at the first -- shows whether a divergence is
        // an isolated, self-correcting blip (position/size realign on the very next leaf, only a handful of
        // fields ever differ) or a full bitstream cascade (position/size stays misaligned from that point
        // on, every subsequent leaf differs) -- the two have very different root causes and this
        // distinguishes them directly instead of requiring a second run per candidate leaf.
        const int maxDivergences = 8;
        int divergencesShown = 0;
        int minCount = Math.Min(referenceLeaves.Count, sortedActual.Count);
        for (int i = 0; i < minCount && divergencesShown < maxDivergences; i++)
        {
            var reference = referenceLeaves[i];
            var actual = sortedActual[i];
            if (reference.R != actual.R || reference.C != actual.C || reference.WidthMi != actual.WidthMi || reference.HeightMi != actual.HeightMi)
            {
                Console.WriteLine($"  Structural divergence at leaf index {i} (position/size):");
                Console.WriteLine($"    reference: {reference}");
                Console.WriteLine($"    actual:    {actual}");
                divergencesShown++;
                continue;
            }

            var fieldDiffs = reference.DiffAgainst(actual).ToList();
            if (fieldDiffs.Count > 0)
            {
                Console.WriteLine($"  Structural divergence at leaf index {i} (r={reference.R}, c={reference.C}, size={reference.WidthMi}x{reference.HeightMi}):");
                foreach (string diff in fieldDiffs)
                {
                    Console.WriteLine($"    {diff}");
                }

                if (actual.EstimatedCost is { } cost)
                {
                    Console.WriteLine($"    (PeachImage's own real-time estimated cost for this leaf: {cost})");
                }

                divergencesShown++;
            }
        }

        if (divergencesShown > 0)
        {
            Console.WriteLine(divergencesShown >= maxDivergences
                ? $"  (stopped after {maxDivergences} divergences -- there may be more.)"
                : $"  ({divergencesShown} total divergence(s) in the first {minCount} position-aligned leaves; every other leaf position/size matched.)");
            return;
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
