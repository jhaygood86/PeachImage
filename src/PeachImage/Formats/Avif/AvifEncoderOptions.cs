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
}
