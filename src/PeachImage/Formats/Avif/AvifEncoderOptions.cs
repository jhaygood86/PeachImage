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
    /// How exhaustively the lossless encoder searches for the smallest output, 0 (slowest, most thorough) to
    /// 9 (fastest, most pruned). Defaults to 2 ("Good Quality"). Mirrors libaom's own <c>--cpu-used</c> under
    /// AV1's ALL_INTRA usage mode -- the mode AVIF still-image encoding uses -- so a given value is meant to
    /// be a direct port of libaom's own per-level search behavior at that <c>cpu-used</c> value, not an
    /// independently-tuned scale (see the project plan, compare-avif-encoding-to-lucky-clover.md).
    ///
    /// <para><b>Validated but not yet load-bearing</b>: this value is threaded through and computed into the
    /// full libaom speed-feature table (every field unit-tested against libaom's own source), but no search
    /// decision consults it yet -- an initial attempt at wiring it into the mode-search prescreen measurably
    /// regressed lossless output size and was reverted rather than shipped as a regression (see
    /// <c>Av1TileEncoder.TileState.SpeedFeatures</c>'s remarks). Every <see cref="Effort"/> value currently
    /// produces identical lossless output; this will change as that work lands.</para>
    ///
    /// <para>Ignored when <see cref="Lossless"/> is <see langword="false"/>: <see cref="Quality"/>-based
    /// encoding does not yet have an equivalent effort knob.</para>
    /// </summary>
    public int Effort { get; init; } = 2;

    /// <summary>
    /// CICP (ITU-T H.273) <c>color_primaries</c> tagged onto the encoded AV1 bitstream. Defaults to 1
    /// (CP_BT_709). This is pure metadata for a decoder/player's own color management -- it never changes
    /// how this encoder converts source RGB pixels to YUV (see <see cref="Lossless"/>'s own remarks for the
    /// two conversions this encoder actually implements), so setting it to a value that doesn't match your
    /// source content's real primaries will make a color-managed player render the image with an incorrect
    /// color transform. Set this only when you know your source pixels were authored in a different color
    /// space than sRGB/BT.709 (e.g. Display P3 = 12, BT.2020 = 9) and want the container to say so honestly.
    /// </summary>
    public int ColorPrimaries { get; init; } = Encoder.Av1.Av1SequenceHeaderWriter.ColorPrimaries;

    /// <summary>
    /// CICP (ITU-T H.273) <c>transfer_characteristics</c> tagged onto the encoded AV1 bitstream. Defaults to
    /// 13 (TC_SRGB). Pure metadata, same caveat as <see cref="ColorPrimaries"/>: this encoder always converts
    /// source pixels as if they were already in the output transfer function's own value domain (no gamma
    /// re-mapping is performed), so this only relabels already-computed pixel values -- it does not perform
    /// a transfer-function conversion.
    /// </summary>
    public int TransferCharacteristics { get; init; } = Encoder.Av1.Av1SequenceHeaderWriter.TransferCharacteristics;

    /// <summary>
    /// AV1 <c>chroma_sample_position</c> (spec §6.4.2), describing where chroma samples are sited relative
    /// to luma for 4:2:0 subsampled content. Defaults to 0 (CSP_UNKNOWN). Only meaningful -- and only ever
    /// written to the bitstream -- for <see cref="Quality"/>-based (non-<see cref="Lossless"/>) encoding,
    /// since <see cref="Lossless"/> always uses 4:4:4 chroma, which has no siting concept. Pure metadata:
    /// this encoder's own 4:2:0 downsampling (<c>Av1RgbToYuvConverter</c>) always uses a fixed box filter
    /// regardless of this value.
    /// </summary>
    public int ChromaSamplePosition { get; init; } = Encoder.Av1.Av1SequenceHeaderWriter.ChromaSamplePosition;

    /// <summary>
    /// When <see langword="true"/>, forces the color image to be encoded as an AV1 monochrome (single Y
    /// plane, no chroma at all) item, even when the source is <see cref="PixelFormat.Rgb24"/> or
    /// <see cref="PixelFormat.Rgba32"/> -- equivalent to real encoders' own <c>--monochrome</c>. Defaults to
    /// <see langword="false"/>. A <see cref="PixelFormat.Gray8"/> source is always encoded monochrome
    /// regardless of this flag (there is no chroma to discard either way).
    ///
    /// <para>For an RGB(A) source, the retained luma channel is computed with the exact same formula the
    /// corresponding non-monochrome encode would have used for Y -- BT.601 (<c>Av1RgbToYuvConverter</c>) when
    /// <see cref="Lossless"/> is <see langword="false"/>, or the identity matrix's <c>Y = G</c> (<c>Av1RgbToYuvIdentityConverter</c>)
    /// when it's <see langword="true"/> -- so this flag only ever removes chroma, never changes what the
    /// retained luma samples are. This intentionally mirrors real encoders' own semantics: discarding chroma
    /// is a deliberate, lossy choice for genuinely colored source content regardless of <see cref="Lossless"/>
    /// (which then only governs whether the retained luma channel itself is coded exactly) -- only set this
    /// for content that is already effectively grayscale, or when chroma loss is explicitly acceptable.
    /// Transparency (<see cref="PixelFormat.Rgba32"/>'s alpha channel, encoded as a separate AV1 image item)
    /// is completely unaffected -- it was already always monochrome on its own.</para>
    /// </summary>
    public bool MonoChrome { get; init; }

    /// <summary>
    /// Whether this encoder's real, content-based auto-detection (a faithful port of libaom's own
    /// <c>estimate_screen_content</c>) is even allowed to enable AV1's screen-content tools -- palette mode
    /// and IntraBC -- at all. Defaults to <see langword="true"/> (auto-detect, this encoder's own long-
    /// standing default behavior, unchanged). Mirrors real aomenc's own <c>--enable-palette</c>/
    /// <c>--enable-intrabc</c> pair, collapsed into one flag here because this encoder's own architecture
    /// gates both tools behind the same shared AV1 <c>allow_screen_content_tools</c> frame flag (spec
    /// requires it for <c>allow_intrabc</c> to be read at all, and it's palette's own real, direct gate too)
    /// -- there is no way to enable one without at least making the frame flag itself available to the
    /// other. Setting this to <see langword="false"/> disables both entirely and skips the real trial-encode
    /// cost the auto-detection otherwise pays to double-check itself. Set <see cref="EnableIntrabc"/>
    /// separately to disable only IntraBC while leaving palette's own detection untouched.
    /// </summary>
    public bool EnableScreenContentTools { get; init; } = true;

    /// <summary>
    /// Whether IntraBC specifically is allowed, independent of <see cref="EnableScreenContentTools"/> (which
    /// must also be <see langword="true"/> for this to matter at all -- IntraBC needs the same frame flag
    /// palette does). Defaults to <see langword="true"/>. Mirrors real aomenc's own <c>--enable-intrabc</c>.
    /// Setting this to <see langword="false"/> leaves palette's own real content-based detection fully
    /// active; there is no equivalent independent "palette only" flag in the other direction, since this
    /// encoder's own palette search has no gate separate from the shared screen-content-tools frame flag.
    /// </summary>
    public bool EnableIntrabc { get; init; } = true;
}
