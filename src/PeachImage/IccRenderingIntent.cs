namespace PeachImage;

/// <summary>
/// An ICC rendering intent (ICC.1:2010 §7.2.15), controlling how an <see cref="IccColorProfile"/> maps colors
/// that fall outside the destination gamut. Pass <see langword="null"/> to <see cref="IccColorProfile.ConvertToSrgb"/>
/// to use the profile's own declared <see cref="IccColorProfile.DefaultRenderingIntent"/> instead of choosing one.
/// </summary>
public enum IccRenderingIntent
{
    /// <summary>Preserves the overall visual relationship between colors, compressing out-of-gamut colors rather than clipping them — usually the best choice for photographic content.</summary>
    Perceptual,

    /// <summary>Maps in-gamut colors exactly and clips out-of-gamut colors to the nearest reproducible color, without rescaling white.</summary>
    RelativeColorimetric,

    /// <summary>Prioritizes vivid, saturated colors over exact accuracy — usually the best choice for business graphics/charts.</summary>
    Saturation,

    /// <summary>Like <see cref="RelativeColorimetric"/>, but also preserves the destination medium's actual white point rather than rescaling to it.</summary>
    AbsoluteColorimetric,
}
