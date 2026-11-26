namespace PeachImage;

/// <summary>A well-known RGB color space PeachImage can describe, embed as a built-in profile and convert pixels to or from.</summary>
public enum NamedColorSpace
{
    /// <summary>IEC 61966-2-1 sRGB: Rec. 709 primaries, D65 white, the piecewise sRGB transfer function.</summary>
    Srgb,

    /// <summary>sRGB primaries and white with a linear transfer function (1.0 = the display's SDR white).</summary>
    LinearSrgb,

    /// <summary>Display P3: DCI-P3 primaries with a D65 white, the sRGB transfer function.</summary>
    DisplayP3,

    /// <summary>Display P3 primaries and white with a linear transfer function.</summary>
    LinearDisplayP3,

    /// <summary>ITU-R BT.2020 primaries, D65 white, with the BT.709/BT.2020 SDR transfer function.</summary>
    Rec2020,

    /// <summary>BT.2020 primaries and white with a linear transfer function.</summary>
    LinearRec2020,

    /// <summary>ITU-R BT.2100 PQ (SMPTE ST 2084): BT.2020 primaries, D65 white, the perceptual quantizer. No ICC profile can be built for it; it is signalled with <see cref="CicpCode"/>.</summary>
    Rec2100Pq,

    /// <summary>ITU-R BT.2100 HLG: BT.2020 primaries, D65 white, the hybrid log-gamma. No ICC profile can be built for it; it is signalled with <see cref="CicpCode"/>.</summary>
    Rec2100Hlg,
}

/// <summary>The electro-optical transfer function that relates a color space's stored values to linear light.</summary>
public enum TransferFunction
{
    /// <summary>Not a recognized function (for example a profile whose tone curve is an arbitrary table).</summary>
    Unknown,

    /// <summary>Stored values are linear light.</summary>
    Linear,

    /// <summary>The piecewise sRGB function (IEC 61966-2-1).</summary>
    Srgb,

    /// <summary>A pure power law; <see cref="ColorSpaceDescription.Gamma"/> holds the exponent.</summary>
    Gamma,

    /// <summary>The ITU-R BT.709 / BT.2020 SDR function.</summary>
    Bt709,

    /// <summary>The SMPTE ST 2084 perceptual quantizer (HDR).</summary>
    Pq,

    /// <summary>The ITU-R BT.2100 hybrid log-gamma (HDR).</summary>
    Hlg,
}

/// <summary>A CIE 1931 xy chromaticity coordinate.</summary>
/// <param name="X">The x coordinate.</param>
/// <param name="Y">The y coordinate.</param>
public readonly record struct ChromaticityXy(double X, double Y);

/// <summary>
/// What an ICC profile, a <see cref="CicpCode"/> or a <see cref="NamedColorSpace"/> says about an RGB space: its primaries, white point
/// and transfer function, plus the named space they match, if any.
/// </summary>
/// <param name="Red">The red primary.</param>
/// <param name="Green">The green primary.</param>
/// <param name="Blue">The blue primary.</param>
/// <param name="White">The white point.</param>
/// <param name="Transfer">The transfer function.</param>
/// <param name="Gamma">The exponent when <paramref name="Transfer"/> is <see cref="TransferFunction.Gamma"/>; otherwise 0.</param>
/// <param name="Named">The <see cref="NamedColorSpace"/> these values match within tolerance, or <see langword="null"/>.</param>
public readonly record struct ColorSpaceDescription(
    ChromaticityXy Red,
    ChromaticityXy Green,
    ChromaticityXy Blue,
    ChromaticityXy White,
    TransferFunction Transfer,
    double Gamma = 0,
    NamedColorSpace? Named = null)
{
    /// <summary>Whether this space's transfer function is an HDR one (<see cref="TransferFunction.Pq"/> or <see cref="TransferFunction.Hlg"/>).</summary>
    public bool IsHdr => Transfer is TransferFunction.Pq or TransferFunction.Hlg;

    /// <summary>Describes <paramref name="space"/>.</summary>
    public static ColorSpaceDescription Of(NamedColorSpace space) => Internal.Color.NamedSpaces.Describe(space);
}

/// <summary>
/// ITU-T H.273 coding-independent code points (the values PNG's <c>cICP</c> chunk and AVIF's <c>nclx</c> box carry) that say which
/// color space image data is in.
/// </summary>
/// <param name="ColorPrimaries">H.273 ColourPrimaries (1 = BT.709/sRGB, 9 = BT.2020, 12 = Display P3).</param>
/// <param name="TransferCharacteristics">H.273 TransferCharacteristics (8 = linear, 13 = sRGB, 16 = PQ, 18 = HLG).</param>
/// <param name="MatrixCoefficients">H.273 MatrixCoefficients (0 = identity, i.e. RGB).</param>
/// <param name="FullRange">Whether samples use the full numeric range.</param>
public readonly record struct CicpCode(byte ColorPrimaries, byte TransferCharacteristics, byte MatrixCoefficients = 0, bool FullRange = true)
{
    /// <summary>The code points for <paramref name="space"/>.</summary>
    public static CicpCode Of(NamedColorSpace space) => space switch
    {
        NamedColorSpace.Srgb => new CicpCode(1, 13),
        NamedColorSpace.LinearSrgb => new CicpCode(1, 8),
        NamedColorSpace.DisplayP3 => new CicpCode(12, 13),
        NamedColorSpace.LinearDisplayP3 => new CicpCode(12, 8),
        NamedColorSpace.Rec2020 => new CicpCode(9, 14),
        NamedColorSpace.LinearRec2020 => new CicpCode(9, 8),
        NamedColorSpace.Rec2100Pq => new CicpCode(9, 16),
        NamedColorSpace.Rec2100Hlg => new CicpCode(9, 18),
        _ => throw new ArgumentOutOfRangeException(nameof(space), space, message: null),
    };

    /// <summary>The <see cref="NamedColorSpace"/> these code points denote, or <see langword="null"/> when they denote none.</summary>
    public NamedColorSpace? ToNamedColorSpace()
    {
        if (MatrixCoefficients != 0)
        {
            return null;
        }

        return (ColorPrimaries, TransferCharacteristics) switch
        {
            (1, 13 or 1 or 6) => NamedColorSpace.Srgb,
            (1, 8) => NamedColorSpace.LinearSrgb,
            (12, 13) => NamedColorSpace.DisplayP3,
            (12, 8) => NamedColorSpace.LinearDisplayP3,
            (9, 14 or 15 or 1) => NamedColorSpace.Rec2020,
            (9, 8) => NamedColorSpace.LinearRec2020,
            (9, 16) => NamedColorSpace.Rec2100Pq,
            (9, 18) => NamedColorSpace.Rec2100Hlg,
            _ => null,
        };
    }

    /// <summary>The transfer function these code points denote.</summary>
    public TransferFunction Transfer => TransferCharacteristics switch
    {
        8 => TransferFunction.Linear,
        13 => TransferFunction.Srgb,
        1 or 6 or 14 or 15 => TransferFunction.Bt709,
        16 => TransferFunction.Pq,
        18 => TransferFunction.Hlg,
        _ => TransferFunction.Unknown,
    };

    /// <summary>Whether these code points denote an HDR signal (PQ or HLG).</summary>
    public bool IsHdr => TransferCharacteristics is 16 or 18;
}
