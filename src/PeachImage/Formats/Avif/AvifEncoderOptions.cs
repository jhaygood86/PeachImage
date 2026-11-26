namespace PeachImage.Formats.Avif;

/// <summary>
/// AVIF-specific encode options. This encoder only ever produces an 8-bit still image in this version --
/// there is deliberately no bit-depth toggle yet, since a silently-ignored knob would be misleading.
/// <see cref="Quality"/>-based (non-lossless) encoding is always 4:2:0/BT.601; <see cref="Lossless"/>
/// switches to 4:4:4 with an identity color matrix instead (see that property's remarks). A source image
/// with real (non-fully-opaque) alpha is encoded as a second, independent monochrome AV1 image item
/// referenced via <c>iref auxl</c>, straight (non-premultiplied) only; a fully opaque
/// <see cref="PixelFormat.Rgba32"/> source is auto-downgraded to plain RGB24 with no alpha item.
/// </summary>
public sealed class AvifEncoderOptions : EncoderOptions
{
    /// <summary>Quality, 0 (worst/smallest) to 100 (best/largest), analogous to JPEG's IJG-style scale. Defaults to 75. Ignored when <see cref="Lossless"/> is <see langword="true"/>.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>
    /// The bit depth of the AV1 stream: 8, 10 or 12. The default, 0, picks 8 for 8-bit sources and 10 for 16-bit and floating-point ones. A
    /// higher-precision source is scaled to this depth (rounded to nearest); a decoded 10-bit or 12-bit AVIF is 16-bit data whose values are exactly
    /// the original samples scaled up, so encoding it at its own depth reproduces them exactly. Lossy and lossless both support all three depths, with palette
    /// mode and intra block copy (the screen-content tools of lossless encoding) at every depth.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 0, 8, 10 or 12.</exception>
    public int BitDepth
    {
        get;
        init
        {
            if (value is not (0 or 8 or 10 or 12))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "BitDepth must be 0 (automatic), 8, 10 or 12.");
            }

            field = value;
        }
    }

    internal override int RequestedBitDepth => BitDepth;

    /// <summary>
    /// <see cref="Quality"/> as a fraction from 0 to 1, mapped the way a browser maps the <c>quality</c> argument of <c>canvas.toBlob(type, quality)</c>:
    /// <c>Quality = round(value x 100)</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or outside 0..1 (a browser would use its default; pass that instead).</exception>
    public float QualityFraction
    {
        get => Quality / 100f;
        init
        {
            Quality = PeachImage.Internal.BrowserQuality.ToPercent(value);
        }
    }

    /// <summary>
    /// When <see langword="true"/>, encodes via AV1's lossless coding path (Walsh-Hadamard transform, no
    /// quantization) instead of DCT_DCT, preserving every pixel exactly -- alpha, when present, is encoded
    /// losslessly too. Defaults to <see langword="false"/>.
    ///
    /// <para>For RGB24/Rgba32 sources, this also switches chroma from this encoder's usual 4:2:0/BT.601 to
    /// 4:4:4 with AV1's identity color matrix (<c>Y=G, Cb=B, Cr=R</c>, no cross-channel math at all -- the
    /// same technique real lossless AVIF encoders, e.g. libavif/aom's <c>--lossless</c>, use) so color detail
    /// isn't discarded by subsampling before the lossless coding step ever runs. This is not a separate
    /// opt-in: a boolean named <c>Lossless</c> that silently kept subsampling chroma would defeat its own
    /// purpose, so setting this to <see langword="true"/> always means genuinely pixel-exact output for
    /// RGB24/Rgba32/Gray8 sources alike. <see cref="Quality"/>-based encoding is completely unaffected and
    /// always stays 4:2:0/BT.601.</para>
    /// </summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// How exhaustively the encoder searches for the smallest output, 0 (slowest, most thorough) to 9
    /// (fastest, most pruned). Defaults to 2. For lossless encoding this mirrors libaom's own
    /// <c>--cpu-used</c> under AV1's ALL_INTRA usage mode (see below).
    ///
    /// <para><b>Lossy</b> (<see cref="Lossless"/> <see langword="false"/>): 0 and 1 try every luma intra mode
    /// and angle delta with full transform and entropy costing (slowest). From 2 upward only the luma
    /// candidates with the best prediction-residual SATD get that full costing (16 at effort 2, 10 at 3-4,
    /// 6 at 5-6, 4 at 7-9), the partition search ranks leaf-vs-split with a much smaller shortlist, and the
    /// chroma search is restricted by the luma winner. The default is roughly 5x faster than effort 0 for
    /// well under 1% larger output on photographic content.</para>
    ///
    /// <para><b>Lossless</b>: validated but not yet load-bearing. This value is threaded through and computed
    /// into the full libaom speed-feature table, but no lossless search decision consults it yet -- an initial
    /// attempt at wiring it into the mode-search prescreen measurably regressed lossless output size and was
    /// reverted. Every <see cref="Effort"/> value currently produces identical lossless output.</para>
    /// </summary>
    public int Effort { get; init; } = 2;

    /// <summary>
    /// Lossy encoding only: <c>log2</c> of the number of tile rows to split the image into (0 = one tile, 3 = up to 8 tile rows).
    /// Tile rows are encoded in parallel, at a small compression cost (no prediction across a tile boundary), so large
    /// images encode much faster. <see langword="null"/> (the default) chooses automatically from the image height alone
    /// -- images under 512 pixels tall stay single-tile -- so the output never depends on the machine. Ignored when
    /// <see cref="Lossless"/> is <see langword="true"/>.
    /// </summary>
    public int? TileRowsLog2 { get; init; }
}
