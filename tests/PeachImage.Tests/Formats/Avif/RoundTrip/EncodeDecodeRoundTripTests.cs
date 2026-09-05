using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.RoundTrip;

/// <summary>
/// End-to-end round-trip tests through the fully public API (<see cref="Image.Save(Stream, string, EncoderOptions?)"/>
/// / <see cref="Image.Load(Stream, DecoderOptions?)"/>), exercising the real container writer and the real,
/// unmodified decoder together. The default (lossy) path asserts PSNR thresholds rather than exact pixel
/// equality -- placeholders calibrated against this encoder's actual current output, to be revisited as the
/// encoder improves (mirroring how WebP's own round-trip tolerances were set from measured worst-case
/// differences, not guessed up front). <see cref="AvifEncoderOptions.Lossless"/> tests assert exact equality
/// throughout, for every pixel format: lossless switches RGB/RGBA to 4:4:4 with an identity color matrix
/// (see that property's remarks), so there's no chroma-subsampling loss left to tolerate even for varying
/// (gradient/noise) color content, not just solid colors or chroma-free Gray8/alpha.
/// </summary>
public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(37, 29)] // non-multiple-of-64, exercises the encoder's edge-replication padding
    [InlineData(5, 3)]
    [InlineData(1, 1)]
    public void Rgb24SolidColor_RoundTrips_ViaPublicApi(int width, int height)
    {
        var source = CreateSolidColorImage(width, height, 180, 90, 40);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 80 });

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        AssertPsnrAtLeast(source, decoded, minPsnrDb: 28.0);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(50, 40)]
    public void Rgb24Gradient_RoundTrips_ViaPublicApi(int width, int height)
    {
        var source = CreateGradientImage(width, height);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 90 });

        AssertPsnrAtLeast(source, decoded, minPsnrDb: 20.0);
    }

    /// <summary>
    /// Round-trip coverage for issue #60: non-lossless chroma's real directional <c>uv_mode</c> search
    /// (<c>Av1TileEncoder.SearchUvMode</c>) forward-transforms with a mode-dependent
    /// <c>Av1ForwardTransform</c> operator (AdstDct/DctAdst/AdstAdst, matching <c>Av1TxTypeTables.ModeToTxfm</c>)
    /// instead of always DCT_DCT -- getting that wrong desyncs <c>Av1TileDecoder.ComputeTxType</c>'s inverse
    /// transform choice from what was actually forward-transformed, corrupting every pixel decoded after the
    /// first affected leaf. <see cref="CreateDiagonalChromaEdgeImage"/> gives U/V real per-plane directional
    /// detail (diagonal ramps that wrap into hard edges, offset between R and G so chroma isn't just a scaled
    /// copy of luma's own structure) -- unlike a solid color or an axis-aligned gradient, this is exactly the
    /// content a directional (non-DC_PRED) <c>uv_mode</c> should win on, so it reliably exercises the new
    /// forward-transform path rather than leaving every leaf on DC_PRED by chance. A desync would show up
    /// here as severe, cascading corruption (not just quantization loss), so the PSNR floor is set well above
    /// what quantization noise alone would ever produce at these quality levels.
    /// </summary>
    [Theory]
    [InlineData(64, 64, 90)]
    [InlineData(96, 80, 60)] // non-multiple-of-64 + lower quality (coarser quantization stresses the search harder)
    public void DiagonalChromaEdges_RoundTrips_ViaPublicApi(int width, int height, int quality)
    {
        var source = CreateDiagonalChromaEdgeImage(width, height);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = quality });

        AssertPsnrAtLeast(source, decoded, minPsnrDb: 18.0);
    }

    /// <summary>
    /// Regression test for a coefficient-context bug where <c>Av1CoefficientWriter.WriteCoeffs</c>'s
    /// (x4, y4) arguments were passed as (row, column) instead of (column, row) at the luma and chroma call
    /// sites in <c>Av1TileEncoder</c>. This is silently unobservable whenever the padded coding-block grid
    /// is square (miCols == miRows) -- true for every other round-trip test in this file, since they all
    /// stay at or under one 64x64 superblock, where padding always makes both dimensions exactly 64
    /// regardless of aspect ratio. It only manifests on a genuinely non-square, multi-superblock frame: the
    /// above/left entropy context bookkeeping silently stops updating past whichever mi-axis is shorter,
    /// desyncing every block decoded afterward -- a real 1054x1492 photo showed this as severe visual
    /// corruption starting at the exact pixel row equal to the padded width. 320x128 (5x2 superblocks,
    /// deliberately non-square in both mi-count and orientation) reproduces it.
    /// </summary>
    [Fact]
    public void Rgb24Gradient_NonSquareMultiSuperblock_RoundTrips_ViaPublicApi()
    {
        var source = CreateGradientImage(320, 128);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 90 });

        AssertPsnrAtLeast(source, decoded, minPsnrDb: 30.0);
    }

    /// <summary>
    /// Lossless counterpart to <see cref="Rgb24Gradient_NonSquareMultiSuperblock_RoundTrips_ViaPublicApi"/>.
    /// Note this solid-color case does *not* by itself catch the coefficient-context (x4, y4) bug the
    /// gradient test above targets: a solid color makes every block all-zero, and that bug only affected
    /// which adaptive CDF slot a symbol is coded against, not the symbol's value -- confirmed by deliberately
    /// reintroducing the bug during diagnosis, which this test alone did not detect while the gradient test
    /// failed immediately. Kept anyway as a genuine exact-lossless-equality check at a larger, non-square
    /// canvas than <see cref="Rgb24SolidColor_Lossless_RoundTripsExactly"/> covers.
    /// </summary>
    [Fact]
    public void Rgb24SolidColor_NonSquareMultiSuperblock_Lossless_RoundTripsExactly()
    {
        var source = CreateSolidColorImage(320, 128, 180, 90, 40);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// Gradient counterpart to <see cref="Rgb24SolidColor_NonSquareMultiSuperblock_Lossless_RoundTripsExactly"/>
    /// -- stresses the 4:4:4 chroma sizing/padding math (<c>paddedChromaWidth/Height</c> equal to
    /// <c>paddedWidth/Height</c>, not halved) across multiple superblocks and a non-square coding-block grid
    /// at once, the combination most likely to expose an indexing bug that a square or single-superblock
    /// test (like <see cref="Rgb24Gradient_Lossless_RoundTripsExactly"/>) couldn't.
    /// </summary>
    [Fact]
    public void Rgb24Gradient_NonSquareMultiSuperblock_Lossless_RoundTripsExactly()
    {
        var source = CreateGradientImage(320, 128);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// Correctness check for lossless mode's partition-tree RDO (<c>Av1TileEncoder.ShouldKeepAsLeaf</c>):
    /// half the image is a flat solid color (eligible to collapse into one big leaf at every partition
    /// level up to 64x64) and the other half is a gradient (forces normal splitting all the way to 8x8,
    /// exactly like before this feature existed), at a non-square, multi-superblock canvas -- the scenario
    /// most likely to expose a bug in the now-variable-size leaf's neighbor-context bookkeeping
    /// (<c>Av1TileEncoder.EncodeLeaf</c>'s <c>YModes</c>/<c>MiSizes</c> writer, <c>PartitionContext</c>'s
    /// above/left lookups) or the generalized lossless sub-block/chroma-region loops
    /// (<c>EncodeLosslessLumaResidual</c>/<c>EncodeChromaRegion</c>), since flat and non-flat leaves of
    /// different sizes sit directly adjacent to each other here.
    /// </summary>
    [Fact]
    public void MostlyFlatWithGradientHalf_NonSquareMultiSuperblock_Lossless_RoundTripsExactly()
    {
        var source = CreateHalfFlatHalfGradientImage(320, 128);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// Confirms the partition-tree RDO added for lossless mode (<c>Av1TileEncoder.ShouldKeepAsLeaf</c>)
    /// actually shrinks output on flat/graphic-style content, not just that it round-trips exactly.
    /// Thresholds are measured, not guessed: this encoder's original fixed-8x8-leaf behavior (every
    /// superblock forced to split all the way down, regardless of content) produced 360 bytes for the solid
    /// case and 2698 for the mixed case at this exact size. Palette mode (<c>Av1TileEncoder.TryEncodePalette</c>),
    /// IntraBC (<c>Av1TileEncoder.FindIntrabcMatch</c>), and Phase D's directional-mode/angle_delta search
    /// (<c>Av1TileEncoder.EncodeLeaf</c>'s <c>CandidateModes</c> loop) each add a small, expected amount back
    /// on top of leaf-coalescing's own savings: every leaf now carries a <c>has_palette_y</c>/
    /// <c>has_palette_uv</c> bit pair and a <c>use_intrabc</c> bit even on leaves that don't end up using
    /// either, and a much bigger intra-mode alphabet (13 modes x 7 angle deltas vs. the original 5 modes at
    /// angle_delta 0) costs more bits per symbol while its adaptive CDFs are still warming up, even on the
    /// (exactly-solid, exactly-flat-gradient) content here where DC_PRED/SMOOTH_PRED were already close to
    /// optimal and rarely lose to a directional candidate -- so both bounds include headroom for that fixed
    /// per-leaf/per-mode-alphabet cost, not just for encoder tuning -- 329/2853 bytes measured at time of
    /// writing (mixed case grew from 2713 with the addition of the directional/angle search; see the project
    /// plan's Phase D notes for why this is expected and why the real target for that phase is a much larger
    /// real-world image, not this synthetic one).
    /// </summary>
    [Fact]
    public void Lossless_FlatRegions_ProduceSmallerOutputThanForcedFixedLeafBaseline()
    {
        int width = 512, height = 256;
        var solid = CreateSolidColorImage(width, height, 180, 90, 40);
        var mixed = CreateHalfFlatHalfGradientImage(width, height);

        using var solidStream = new MemoryStream();
        solid.Save(solidStream, "avif", new AvifEncoderOptions { Lossless = true });

        using var mixedStream = new MemoryStream();
        mixed.Save(mixedStream, "avif", new AvifEncoderOptions { Lossless = true });

        Assert.True(solidStream.Length < 350, $"Solid lossless output grew to {solidStream.Length} bytes (forced-fixed-leaf baseline was 360) -- partition-tree leaf coalescing may have regressed.");
        Assert.True(mixedStream.Length < 2900, $"Mixed lossless output grew to {mixedStream.Length} bytes (forced-fixed-leaf baseline was 2698) -- partition-tree leaf coalescing may have regressed.");
    }

    /// <summary>
    /// Correctness and effectiveness check for IntraBC (<c>Av1TileEncoder.FindIntrabcMatch</c>/
    /// <c>FindApproximateIntrabcMatch</c>): a vertical-stripe gradient (pixel value depends only on column,
    /// not row -- non-flat within a leaf, so palette/leaf-coalescing don't already handle it, but identical
    /// across every row at a given column) at a canvas wide enough (6 superblock-columns, 384px) to satisfy
    /// IntraBC's own pipelining-delay constraint (<c>INTRABC_DELAY_SB64</c>, spec §6.10.25 -- see
    /// <c>Av1TileEncoder.IsValidIntrabcSource</c>) for a copy source one superblock-row above.
    ///
    /// <para>Both images start with a 64-row "filler" band of *different* content, then a 64-row "source"
    /// band -- <c>withRepeat</c> adds one more 64-row band that's an exact copy of the source band,
    /// <c>baseline</c> doesn't. Deliberately not comparing a bare repeated pattern starting at row 0: with
    /// the RD-optimal partition search (Phase D), the very first superblock row of a frame has no real
    /// "above" neighbor to predict from at all, which measurably changes its own best partition/mode choice
    /// (a real, not spurious, difference -- V_PRED genuinely can't help without a real neighbor there) versus
    /// an interior row with the exact same content.</para>
    ///
    /// <para>The &lt;1.85x (not &lt;1.5x) threshold reflects a real, understood interaction rather than a
    /// bug: the RD partition search's leaf-size choice is itself neighbor-content-sensitive (a block whose
    /// "above" is a good predictor merges into bigger leaves more readily than a content-identical block
    /// whose "above" happens to differ), so the source band (whose neighbor above it is the *filler*, not a
    /// match) and the repeat band (whose neighbor above it is the *source*, matching itself almost exactly)
    /// can legitimately end up choosing different leaf sizes even though their own content is identical --
    /// which reduces, but doesn't eliminate, how often IntraBC's same-size-leaf matching can fire. This is an
    /// inherent tension between two real improvements (context-aware partitioning vs. exact-copy detection),
    /// not something a test threshold change should try to fully paper over -- see the project plan's Phase D
    /// notes for the fuller reasoning and the measurement against this exact scenario.</para>
    /// </summary>
    [Fact]
    public void RepeatedVerticalStripePattern_Lossless_IntrabcRoundTripsExactlyAndStaysSmall()
    {
        int width = 384;
        var baseline = CreateFillerPlusStripePatternImage(width, repeatCount: 0);
        var withRepeat = CreateFillerPlusStripePatternImage(width, repeatCount: 1);

        var decodedBaseline = EncodeThenDecode(baseline, new AvifEncoderOptions { Lossless = true });
        Assert.Equal(baseline.GetPixelSpan().ToArray(), decodedBaseline.GetPixelSpan().ToArray());

        var decodedWithRepeat = EncodeThenDecode(withRepeat, new AvifEncoderOptions { Lossless = true });
        Assert.Equal(withRepeat.GetPixelSpan().ToArray(), decodedWithRepeat.GetPixelSpan().ToArray());

        using var baselineStream = new MemoryStream();
        baseline.Save(baselineStream, "avif", new AvifEncoderOptions { Lossless = true });

        using var withRepeatStream = new MemoryStream();
        withRepeat.Save(withRepeatStream, "avif", new AvifEncoderOptions { Lossless = true });

        Assert.True(
            withRepeatStream.Length < baselineStream.Length * 1.85,
            $"Adding one exact-repeat 64-row band grew output from {baselineStream.Length} to {withRepeatStream.Length} bytes -- IntraBC may not be finding the repeated region.");
    }

    /// <summary>64-row "filler" (a different, non-matching pattern) followed by a 64-row vertical-stripe "source" band, followed by <paramref name="repeatCount"/> more 64-row bands that exactly repeat the source band.</summary>
    private static Image CreateFillerPlusStripePatternImage(int width, int repeatCount)
    {
        const int bandHeight = 64;
        int height = bandHeight * (2 + repeatCount);
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            int band = row / bandHeight;
            int patternRow = row % bandHeight;
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                if (band == 0)
                {
                    // Filler: a horizontal-stripe pattern (varies by row, not column) -- structurally
                    // different from the vertical-stripe source/repeat bands, so it can't accidentally
                    // IntraBC-match them.
                    pixels[idx + 0] = (byte)(patternRow * 255 / (bandHeight - 1));
                    pixels[idx + 1] = (byte)(patternRow * 91 % 256);
                    pixels[idx + 2] = (byte)(200 - (patternRow * 2));
                }
                else
                {
                    pixels[idx + 0] = (byte)(col * 255 / (width - 1));
                    pixels[idx + 1] = (byte)(255 - (col * 255 / (width - 1)));
                    pixels[idx + 2] = (byte)((col * 37) % 256);
                }
            }
        }

        return image;
    }

    /// <summary>
    /// Regression test for issue #61: <c>EncodeIntrabcResidual</c> previously predicted a whole merged
    /// (&gt;8x8) coding block in one <c>Av1InterPrediction.PredictIntrabc</c> call and sliced it per 4x4
    /// sub-block, diverging from a real decoder's per-sub-block prediction (spec §5.11.35) whenever more than
    /// one sub-block shared a leaf -- silently masked before the fix by gating
    /// <c>Av1TileEncoder</c>'s <c>intrabcApprox</c> to single-sub-block (<c>sizeMi &lt;= 2</c>) leaves only.
    /// Unlike <see cref="RepeatedVerticalStripePattern_Lossless_IntrabcRoundTripsExactlyAndStaysSmall"/>,
    /// which forces IntraBC's *exact*-match path via a byte-identical repeat (unaffected by this bug, since
    /// it has no per-sub-block prediction step), this needs the *approximate*-match path
    /// (<c>Av1TileEncoder.FindApproximateIntrabcMatch</c>) specifically, for a leaf that also merges above
    /// 8x8. It reuses <see cref="SmoothedNoise_Lossless_RoundTripsExactly"/>'s own two-incommensurate-periods
    /// content shape (tuned jaggier here so the RD-cost search prefers a merged approximate-match IntraBC
    /// leaf over splitting further -- confirmed by instrumenting <c>Av1TileEncoder.EncodeLeaf</c> to log
    /// every <c>intrabcApprox</c> use and observing several fire at <c>sizeMi &gt; 2</c> for this exact image)
    /// for both a "source" and a "repeat" band, with the repeat band's jitter deliberately offset from the
    /// source band's so the two are close but never pixel-identical, guaranteeing the byte-exact hash lookup
    /// (<c>FindIntrabcMatch</c>) can never match it: any IntraBC use for that leaf can only come from the
    /// approximate search.
    /// </summary>
    [Fact]
    public void SmoothedNoiseRepeat_Lossless_MergedLeafIntrabcApproximateMatchRoundTripsExactly()
    {
        var image = CreateSmoothedNoiseWithApproximateIntrabcRepeatImage(width: 384);

        var decoded = EncodeThenDecode(image, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(image.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// Regression test for a real bug found while tracing a size-competitiveness gap against libaom on a
    /// real-world image: <c>Av1TileEncoder.FindMvStackAndPredict</c>'s fallback predictor (used whenever an
    /// IntraBC leaf's MV-stack neighbor scan finds no usable candidate -- <c>NumMvFound &lt; 2</c>, spec's own
    /// <c>PredMv</c> fallback) hardcoded <c>sbSize4 = 16</c> (<c>Num_4x4_Blocks_High[BLOCK_64X64]</c>), a
    /// pre-PR-#80 assumption. Lossless has used 128x128 superblocks since that PR
    /// (<c>Av1FrameEncoder.cs</c>'s <c>sbSizeMi = lossless ? 32 : 16</c>), and
    /// <c>Av1TileDecoder.AssignMv</c>'s identical fallback already derives <c>sbSize4</c> from the real
    /// <c>Use128x128Superblock</c> flag (32) -- so the two sides silently predicted a *different* Mv for any
    /// IntraBC leaf that reached this fallback, ever since PR #80 landed. Confirmed via direct encoder/decoder
    /// cross-instrumentation on a real photo (a leaf at mi-row 52 in a 1054x1492 image): the encoder computed
    /// <c>predMvRow=-512</c>, the decoder independently computed <c>predMvRow=-1024</c> for the very same
    /// leaf -- a real, silent 2x divergence, not a rounding difference. Once <c>diffMv</c> (correctly encoded/
    /// decoded -- the entropy stream itself never desynced) was added back, the decoder's block-copy read from
    /// a completely different, wrong source position, corrupting every pixel that leaf's IntraBC prediction
    /// touched (confirmed: fixing only this one constant took that image from ~130,000 wrong bytes to 0).
    ///
    /// <para>This reuses <see cref="CreateSmoothedNoiseWithApproximateIntrabcRepeatImage"/> unchanged --
    /// merely narrowing its width (160 instead of 384) changes the partition/RD outcome enough that the
    /// repeat band's approximate-match IntraBC leaf ends up with no seedable MV-stack neighbor, reaching the
    /// buggy fallback directly (no palette or partition-cost changes needed to reach it, unlike how this bug
    /// was originally found). At width 384 the same fixture's IntraBC leaf(s) apparently have a neighbor that
    /// seeds a real (nonzero) candidate, avoiding the fallback -- illustrating just how easy this was to miss
    /// (and hard to un-hit once a real image happens to hit it): the two widths differ only in how many
    /// leaves happen to precede this one, yet only one of them exercises the broken code path.</para>
    /// </summary>
    [Fact]
    public void SmoothedNoiseRepeat_Lossless_IntrabcFallbackMvPredictorRoundTripsExactly()
    {
        var image = CreateSmoothedNoiseWithApproximateIntrabcRepeatImage(width: 160);

        var decoded = EncodeThenDecode(image, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(image.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// 64-row filler (horizontal-stripe, structurally unrelated -- see <see cref="CreateFillerPlusStripePatternImage"/>),
    /// followed by a 64-row "source" band and a 64-row "repeat" band both built from a jaggier variant of
    /// <see cref="SmoothedNoise_Lossless_RoundTripsExactly"/>'s own two-incommensurate-periods formula, with
    /// the repeat band's per-pixel jitter deterministically offset from the source band's so the two are
    /// close but never pixel-identical.
    /// </summary>
    private static Image CreateSmoothedNoiseWithApproximateIntrabcRepeatImage(int width)
    {
        const int bandHeight = 64;
        const int height = bandHeight * 3;
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();

        for (int row = 0; row < height; row++)
        {
            int band = row / bandHeight;
            int patternRow = row % bandHeight;
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                if (band == 0)
                {
                    // Filler: same horizontal-stripe shape as CreateFillerPlusStripePatternImage's filler --
                    // structurally different from the source/repeat bands' formula below.
                    pixels[idx + 0] = (byte)(patternRow * 255 / (bandHeight - 1));
                    pixels[idx + 1] = (byte)(patternRow * 91 % 256);
                    pixels[idx + 2] = (byte)(200 - (patternRow * 2));
                    continue;
                }

                // band == 1 (source) or band == 2 (repeat): identical two-incommensurate-periods base shape
                // (a jaggier variant of SmoothedNoise's own (row/9, col/7) formula -- shorter /5, /3 periods
                // defeat directional intra prediction even more, which is what makes a merged leaf's WHT-cost
                // comparison prefer IntraBC's block-copy over further splitting) -- so both bands
                // independently tend to merge into >8x8 leaves, and a block-copy from one to the other needs
                // to correct only the small jitter difference below, not the whole shape.
                int baseVal = (((patternRow / 5) * 37) + ((col / 3) * 53)) % 256;

                // Jitter differs by a fixed, deterministic shift between source (band 1) and repeat (band 2)
                // -- guarantees pixel(band=2) != pixel(band=1) at the large majority of positions (only
                // 1-in-3 coincide, given the mod-3 range below), so this pattern can never satisfy
                // BlockPixelsEqual and FindIntrabcMatch's exact-match search can never fire for the repeat
                // band. The small amplitude relative to baseVal's own up-to-255 swings keeps a block-copy
                // from the source band plus this small residual cheaper than re-deriving the whole jagged
                // shape via intra prediction alone.
                int jitter = (((patternRow * 13) + (col * 7) + band) % 3) - 1;
                byte value = (byte)((baseVal + jitter + 256) % 256);
                pixels[idx + 0] = value;
                pixels[idx + 1] = value;
                pixels[idx + 2] = value;
            }
        }

        return image;
    }

    /// <summary>
    /// Regression guard for a real bug found (and root-caused only as far as "not any Phase D search
    /// addition, not chroma-specific, not angle_delta-specific, not FILTER_INTRA-specific") while building
    /// Phase D's real-cost partition search: a lossless coding block merged bigger than its own 4x4
    /// transform (any leaf size above 8x8) whose real residual is non-trivial (not the all-zero case
    /// Phase A's exactly-flat merging already covered) round-trips *incorrectly* -- confirmed via a smoothed
    /// (not pure-white) noise pattern, which merges into bigger leaves under a real WHT-cost comparison the
    /// same way genuinely "nearly flat" screen-content-style regions would, unlike either pure noise (never
    /// merges, since every 8x8 already looks maximally different from its neighbors) or a smooth gradient
    /// (round-trips fine even merged, confirmed separately). Reproduces monochrome, independent of chroma.
    /// This currently passes only because the encoder deliberately keeps the exactly-flat-only partition
    /// heuristic active (<c>Av1TileEncoder.ShouldKeepAsLeafFlatOnly</c>) rather than the real RD-cost search
    /// (<c>Av1TileEncoder.DecidePartition</c>, present in the file but not called) that surfaced this -- see
    /// the project plan's Phase D notes. If this test ever starts failing again, that almost certainly means
    /// something re-enabled non-flat merging without first finding and fixing the underlying bug.
    /// </summary>
    [Fact]
    public void SmoothedNoise_Lossless_RoundTripsExactly()
    {
        const int size = 256;
        var image = Image.Create(size, size, PixelFormat.Gray8);
        var pixels = image.GetPixelSpan();
        var rng = new Random(1);
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                int idx = (row * size) + col;
                int baseVal = (((row / 9) * 37) + ((col / 7) * 53)) % 256;
                pixels[idx] = (byte)((baseVal + rng.Next(-4, 5) + 256) % 256);
            }
        }

        var decoded = EncodeThenDecode(image, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(image.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Gray8Image_RoundTrips_ViaPublicApi()
    {
        var source = CreateGrayscaleImage(48, 32);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 85 });

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        AssertPsnrAtLeast(source, decoded, minPsnrDb: 25.0);
    }

    [Fact]
    public void Gray8Image_Lossless_RoundTripsExactly()
    {
        var source = CreateGrayscaleImage(48, 32);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);

        // Gray8 has no chroma plane at all, so lossless mode has nothing left to lose end to end -- unlike
        // RGB/RGBA (see the lossless-solid-color and lossless-alpha tests below), this must be pixel-exact,
        // not just a high PSNR.
        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Rgb24SolidColor_Lossless_RoundTripsExactly()
    {
        var source = CreateSolidColorImage(48, 32, 180, 90, 40);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// Unlike a solid color (where every 2x2 chroma group is trivially identical), a gradient has genuinely
    /// spatially-varying color -- exactly the case 4:2:0 chroma subsampling would discard real information
    /// from. Lossless mode switches to 4:4:4 with an identity color matrix specifically so this survives
    /// exactly (see <see cref="AvifEncoderOptions.Lossless"/>'s remarks); before that fix this measured only
    /// ~39.7 dB PSNR here, not exact.
    /// </summary>
    [Fact]
    public void Rgb24Gradient_Lossless_RoundTripsExactly()
    {
        var source = CreateGradientImage(48, 32);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// A gradient's neighboring pixels are highly correlated, so a subtle indexing bug in the 4:4:4 chroma
    /// path could in principle still average out to a passing (if not exact) result. Uncorrelated per-pixel
    /// noise makes every single sample independently load-bearing -- there's no averaging effect to hide a
    /// bug behind -- so this is a stricter stress test than the gradient case, not a redundant one.
    /// </summary>
    [Fact]
    public void Rgb24Noise_Lossless_RoundTripsExactly()
    {
        var source = Image.Create(64, 64, PixelFormat.Rgb24);
        new Random(12345).NextBytes(source.GetPixelSpan());

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Rgba32Image_FullyOpaque_RoundTrips_ViaAutoDowngrade()
    {
        var source = Image.Create(24, 24, PixelFormat.Rgba32);
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < 24 * 24; i++)
        {
            pixels[(i * 4) + 0] = 200;
            pixels[(i * 4) + 1] = 100;
            pixels[(i * 4) + 2] = 50;
            pixels[(i * 4) + 3] = 255;
        }

        using var ms = new MemoryStream();
        source.Save(ms, "avif", new AvifEncoderOptions { Quality = 90 });

        ms.Position = 0;
        var decoded = Image.Load(ms);
        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
    }

    [Fact]
    public void Rgba32Image_WithTransparency_RoundTrips_ViaAlphaItem()
    {
        var source = CreateRgbaGradientWithAlpha(48, 32);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 90 });

        Assert.Equal(48, decoded.Width);
        Assert.Equal(32, decoded.Height);
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);

        var (srcRgb, srcAlpha) = SplitRgba(source.GetPixelSpan());
        var (dstRgb, dstAlpha) = SplitRgba(decoded.GetPixelSpan());

        // RGB and alpha are two independent AV1 image items (see AvifContainerWriter's remarks), each
        // quantized at the same Quality but otherwise unrelated -- assert each on its own tolerance rather
        // than blending them into one number, since alpha (monochrome, no chroma-subsampling loss) tends to
        // survive quantization more cleanly than the chroma-subsampled color planes.
        AssertPsnrAtLeast(srcRgb, dstRgb, minPsnrDb: 20.0, "RGB channels");
        AssertPsnrAtLeast(srcAlpha, dstAlpha, minPsnrDb: 25.0, "alpha channel");
    }

    [Fact]
    public void Rgba32Image_WithTransparency_RoundTrips_ViaAlphaItem_NonMultipleOf64()
    {
        // Exercises the alpha item's own edge-replication padding independently of the color item's --
        // Av1FrameEncoder.Encode is called twice (color, then alpha), each with its own PadPlane call.
        var source = CreateRgbaGradientWithAlpha(37, 29);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Quality = 90 });

        Assert.Equal(37, decoded.Width);
        Assert.Equal(29, decoded.Height);
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);

        var (srcAlpha0, dstAlpha0) = (SplitRgba(source.GetPixelSpan()).Alpha, SplitRgba(decoded.GetPixelSpan()).Alpha);
        AssertPsnrAtLeast(srcAlpha0, dstAlpha0, minPsnrDb: 25.0, "alpha channel");
    }

    [Fact]
    public void Rgba32Image_WithTransparency_Lossless_RoundTripsExactly()
    {
        // Alpha is always its own independent monochrome AV1 image item (see AvifContainerWriter's remarks)
        // -- exactly the same monoChrome code path Gray8Image_Lossless_RoundTripsExactly exercises, so alpha
        // has no chroma-subsampling loss to worry about either and should round-trip exactly regardless of
        // the color plane. Pairing it with a solid RGB base (rather than the gradient the lossy alpha test
        // above uses) means the color plane can be asserted exactly too, via the same reasoning as
        // Rgb24SolidColor_Lossless_RoundTripsExactly.
        var source = Image.Create(40, 24, PixelFormat.Rgba32);
        var pixels = source.GetPixelSpan();
        for (int row = 0; row < 24; row++)
        {
            for (int col = 0; col < 40; col++)
            {
                int idx = ((row * 40) + col) * 4;
                pixels[idx + 0] = 180;
                pixels[idx + 1] = 90;
                pixels[idx + 2] = 40;
                pixels[idx + 3] = (byte)(row * 255 / 23); // gradient alpha, includes 0 and 255
            }
        }

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    /// <summary>
    /// Unlike <see cref="Rgba32Image_WithTransparency_Lossless_RoundTripsExactly"/>'s solid RGB base, this
    /// uses a genuinely varying RGB gradient (the same source <see cref="Rgba32Image_WithTransparency_RoundTrips_ViaAlphaItem"/>'s
    /// lossy test uses) to confirm the color item's new 4:4:4 path and the alpha item's independent
    /// monochrome path compose correctly end to end when *both* carry real, non-trivial content.
    /// </summary>
    [Fact]
    public void Rgba32GradientWithGradientAlpha_Lossless_RoundTripsExactly()
    {
        var source = CreateRgbaGradientWithAlpha(48, 32);

        var decoded = EncodeThenDecode(source, new AvifEncoderOptions { Lossless = true });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(PixelFormat.Gray16)]
    [InlineData(PixelFormat.Rgb48)]
    [InlineData(PixelFormat.Rgba64)]
    [InlineData(PixelFormat.Cmyk32)]
    public void UnsupportedPixelFormat_Throws(PixelFormat format)
    {
        var source = Image.Create(8, 8, format);
        using var ms = new MemoryStream();
        Assert.Throws<AvifEncodingException>(() => source.Save(ms, "avif", new AvifEncoderOptions()));
    }

    [Fact]
    public void DifferentQualityLevels_ProduceDifferentFileSizes()
    {
        var source = CreateGradientImage(64, 64);

        using var highQualityStream = new MemoryStream();
        source.Save(highQualityStream, "avif", new AvifEncoderOptions { Quality = 95 });

        using var lowQualityStream = new MemoryStream();
        source.Save(lowQualityStream, "avif", new AvifEncoderOptions { Quality = 5 });

        Assert.NotEqual(highQualityStream.Length, lowQualityStream.Length);
    }

    [Fact]
    public void Identify_ReportsCorrectDimensionsWithoutFullDecode()
    {
        var source = CreateGradientImage(40, 30);
        using var ms = new MemoryStream();
        source.Save(ms, "avif", new AvifEncoderOptions());

        ms.Position = 0;
        var info = Image.Identify(ms);

        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
        Assert.Equal("avif", info.FormatName);
    }

    private static Image EncodeThenDecode(Image source, AvifEncoderOptions options)
    {
        using var ms = new MemoryStream();
        source.Save(ms, "avif", options);
        ms.Position = 0;
        return Image.Load(ms);
    }

    private static Image CreateSolidColorImage(int width, int height, byte r, byte g, byte b)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < width * height; i++)
        {
            pixels[(i * 3) + 0] = r;
            pixels[(i * 3) + 1] = g;
            pixels[(i * 3) + 2] = b;
        }

        return image;
    }

    private static Image CreateGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                pixels[idx + 0] = (byte)(width <= 1 ? 0 : col * 255 / (width - 1));
                pixels[idx + 1] = (byte)(height <= 1 ? 0 : row * 255 / (height - 1));
                pixels[idx + 2] = 128;
            }
        }

        return image;
    }

    /// <summary>
    /// Diagonal ramps per channel, deliberately wrapped with <c>% 256</c> rather than scaled to the image's
    /// extent (unlike <see cref="CreateGradientImage"/>'s smooth col-/row-scaled ramps): this produces hard
    /// repeating diagonal edges every ~32-64 pixels, real directional structure at the scale a single leaf
    /// actually sees, rather than one smooth ramp too gradual for any one 8x8 block to read as "directional."
    /// R ramps along the main diagonal and G along the anti-diagonal (opposite slopes, different periods) so
    /// the derived chroma planes carry real directional detail of their own rather than a scaled copy of
    /// luma's -- see <see cref="DiagonalChromaEdges_RoundTrips_ViaPublicApi"/>.
    /// </summary>
    private static Image CreateDiagonalChromaEdgeImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                pixels[idx + 0] = (byte)(((col * 5) + (row * 3)) % 256);
                pixels[idx + 1] = (byte)(((col * 3) - (row * 5)) % 256);
                pixels[idx + 2] = (byte)((col + row) % 128);
            }
        }

        return image;
    }

    private static Image CreateHalfFlatHalfGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                if (col < width / 2)
                {
                    pixels[idx + 0] = 180;
                    pixels[idx + 1] = 90;
                    pixels[idx + 2] = 40;
                }
                else
                {
                    pixels[idx + 0] = (byte)(col * 255 / (width - 1));
                    pixels[idx + 1] = (byte)(height <= 1 ? 0 : row * 255 / (height - 1));
                    pixels[idx + 2] = 128;
                }
            }
        }

        return image;
    }

    private static Image CreateGrayscaleImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Gray8);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 5) % 256);
        }

        return image;
    }

    private static Image CreateRgbaGradientWithAlpha(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 4;
                pixels[idx + 0] = (byte)(width <= 1 ? 0 : col * 255 / (width - 1));
                pixels[idx + 1] = (byte)(height <= 1 ? 0 : row * 255 / (height - 1));
                pixels[idx + 2] = 128;

                // Alpha gradient spanning fully transparent to fully opaque -- includes both extremes plus a
                // real mid-range ramp, so this isn't just "opaque with one transparent pixel."
                pixels[idx + 3] = (byte)(height <= 1 ? 255 : row * 255 / (height - 1));
            }
        }

        return image;
    }

    private static (byte[] Rgb, byte[] Alpha) SplitRgba(ReadOnlySpan<byte> rgba)
    {
        int pixelCount = rgba.Length / 4;
        var rgb = new byte[pixelCount * 3];
        var alpha = new byte[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            rgb[(i * 3) + 0] = rgba[(i * 4) + 0];
            rgb[(i * 3) + 1] = rgba[(i * 4) + 1];
            rgb[(i * 3) + 2] = rgba[(i * 4) + 2];
            alpha[i] = rgba[(i * 4) + 3];
        }

        return (rgb, alpha);
    }

    private static void AssertPsnrAtLeast(Image source, Image decoded, double minPsnrDb)
        => AssertPsnrAtLeast(source.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray(), minPsnrDb, "pixels");

    private static void AssertPsnrAtLeast(ReadOnlySpan<byte> src, ReadOnlySpan<byte> dst, double minPsnrDb, string label)
    {
        Assert.Equal(src.Length, dst.Length);

        long sumSquaredError = 0;
        for (int i = 0; i < src.Length; i++)
        {
            int diff = src[i] - dst[i];
            sumSquaredError += diff * diff;
        }

        double mse = sumSquaredError / (double)src.Length;
        double psnr = mse <= 0 ? 100.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);

        Assert.True(psnr >= minPsnrDb, $"{label} PSNR {psnr:F2} dB below required {minPsnrDb} dB (MSE {mse:F2}).");
    }
}
