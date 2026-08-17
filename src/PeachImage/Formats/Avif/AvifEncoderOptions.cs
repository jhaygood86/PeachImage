namespace PeachImage.Formats.Avif;

/// <summary>
/// AVIF-specific encode options. This encoder only ever produces a lossy, 8-bit, 4:2:0, single-item still
/// image in this version -- there is deliberately no lossless/bit-depth/subsampling toggle yet, since a
/// silently-ignored knob would be misleading. Source images with alpha are rejected (see
/// <see cref="AvifUnsupportedFeatureException"/>) rather than silently flattened. Adding these later is a
/// non-breaking new <see langword="init"/> property whenever the encoder's scope grows to support them.
/// </summary>
public sealed class AvifEncoderOptions : EncoderOptions
{
    /// <summary>Quality, 0 (worst/smallest) to 100 (best/largest), analogous to JPEG's IJG-style scale. Defaults to 75.</summary>
    public int Quality { get; init; } = 75;
}
