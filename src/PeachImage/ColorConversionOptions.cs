namespace PeachImage;

/// <summary>How <see cref="Image.ConvertColorSpace(NamedColorSpace, ColorConversionOptions?)"/> treats colors that fall outside the destination's range.</summary>
public enum GamutHandling
{
    /// <summary>Clip: each destination channel is clamped to 0..1 after encoding, so out-of-gamut colors saturate. Works with every destination pixel format.</summary>
    Clip,

    /// <summary>
    /// Keep out-of-gamut colors as values below 0 or above 1 (the transfer functions extend symmetrically around zero), so a later conversion back
    /// restores them. Needs a floating-point destination <see cref="PixelFormat"/>, because integer formats cannot hold them.
    /// </summary>
    Preserve,
}

/// <summary>Options for <see cref="Image.ConvertColorSpace(NamedColorSpace, ColorConversionOptions?)"/>.</summary>
public sealed class ColorConversionOptions
{
    /// <summary>What happens to colors outside the destination gamut. Default <see cref="GamutHandling.Clip"/>.</summary>
    public GamutHandling GamutHandling { get; set; } = GamutHandling.Clip;

    /// <summary>
    /// A profile to treat as the source space, overriding anything the image declares. By default the source is, in order: this, then
    /// <see cref="SourceColorSpace"/>, then the image's embedded ICC profile, then its <see cref="ImageMetadata.Cicp"/> code points, then <see cref="AssumedSourceColorSpace"/>.
    /// </summary>
    public IccColorProfile? SourceProfile { get; set; }

    /// <summary>A named space to treat as the source, overriding anything the image declares.</summary>
    public NamedColorSpace? SourceColorSpace { get; set; }

    /// <summary>The space assumed when the image declares none (no usable ICC profile and no recognized cICP). Default <see cref="NamedColorSpace.Srgb"/>.</summary>
    public NamedColorSpace AssumedSourceColorSpace { get; set; } = NamedColorSpace.Srgb;

    /// <summary>The rendering intent for a LUT-based source profile; <see langword="null"/> uses the profile's own. Matrix/TRC profiles are not affected.</summary>
    public IccRenderingIntent? RenderingIntent { get; set; }

    /// <summary>
    /// The pixel format of the result. By default the source's own format, except that gray formats become the RGB format of the same sample
    /// type (a color space conversion of gray data is a color conversion). Must be an RGB or RGBA format.
    /// </summary>
    public PixelFormat? TargetPixelFormat { get; set; }

    /// <summary>The most threads the conversion may use: -1 leaves it to the library, 1 converts on the calling thread.</summary>
    public int MaxDegreeOfParallelism { get; set; } = -1;

    /// <summary>An optional scheduler for the parallel work; <see langword="null"/> uses the thread pool.</summary>
    public TaskScheduler? TaskScheduler { get; set; }

    /// <summary>
    /// What to do with an HDR source (PQ or HLG, known from its cICP code points or <see cref="SourceColorSpace"/>): keep the headroom
    /// (<see cref="HdrToneMapping.Preserve"/>, the default) or tone-map to standard dynamic range. An HLG source always has the BT.2100 system gamma
    /// (1.2, 1000 cd/m2 display) applied, and an HLG destination has it removed.
    /// </summary>
    public HdrToneMapping HdrToneMapping { get; set; }

    /// <summary>The peak luminance in cd/m2 of the HDR content, which <see cref="HdrToneMapping.ToSdr"/> maps to white. Default 1000.</summary>
    public double HdrPeakNits { get; set; } = 1000;
}

/// <summary>Options for <see cref="Image.ConvertTo(PixelFormat, PixelConversionOptions)"/>.</summary>
/// <remarks>
/// Without options a conversion carries values as stored. The rules for a float to integer conversion are always: round to nearest (ties up),
/// clip to the format's range, NaN becomes 0.
/// </remarks>
public sealed class PixelConversionOptions
{
    /// <summary>
    /// Treat the color channels as linear light and apply the sRGB transfer function before storing, the step that takes linear float data to
    /// an 8-bit or 16-bit sRGB image. Alpha is never encoded.
    /// </summary>
    public bool EncodeSrgb { get; set; }

    /// <summary>
    /// The reverse: treat the stored color channels as sRGB-encoded and store linear light, for taking an 8-bit or 16-bit sRGB image to linear float
    /// data. Mutually exclusive with <see cref="EncodeSrgb"/>.
    /// </summary>
    public bool DecodeSrgb { get; set; }

    /// <summary>The most threads the conversion may use: -1 leaves it to the library, 1 converts on the calling thread.</summary>
    public int MaxDegreeOfParallelism { get; set; } = -1;

    /// <summary>An optional scheduler for the parallel work; <see langword="null"/> uses the thread pool.</summary>
    public TaskScheduler? TaskScheduler { get; set; }
}

/// <summary>What <see cref="ColorConversionOptions.HdrToneMapping"/> does with HDR (PQ or HLG) sources.</summary>
public enum HdrToneMapping
{
    /// <summary>
    /// Keep the headroom: highlights come out above 1.0, where 1.0 is SDR reference white (203 cd/m2, ITU-R BT.2408). Meant for floating-point
    /// destinations (<see cref="PixelFormat.RgbaF32"/>, <see cref="PixelFormat.RgbaF16"/>); an integer destination clips them.
    /// </summary>
    Preserve,

    /// <summary>
    /// Compress the highlights into 0..1 with extended Reinhard tone mapping on luminance (colors keep their ratios), so an HDR image becomes a
    /// standard dynamic range one. <see cref="ColorConversionOptions.HdrPeakNits"/> says which luminance maps to white. SDR sources are unchanged.
    /// </summary>
    ToSdr,
}
