namespace PeachImage;

/// <summary>The resampling filter used to reconstruct pixel values when resizing an <see cref="Image"/>.</summary>
public enum ResamplingFilter
{
    /// <summary>Keys' cubic convolution (a = -0.5) — a good general-purpose default; matches most photo editors' "Bicubic". Numerically identical to <see cref="CatmullRom"/>.</summary>
    Bicubic,

    /// <summary>Nearest-pixel averaging filter: fast, but blurs upscales and can alias downscales more than the other filters here.</summary>
    Box,

    /// <summary>The generalized cubic convolution filter with (B, C) = (0, 0.5) — a common sharper alternative to <see cref="Bicubic"/>'s identical formula.</summary>
    CatmullRom,

    /// <summary>The generalized cubic convolution filter with (B, C) = (0, 0), a softer cubic than <see cref="CatmullRom"/>.</summary>
    Hermite,

    /// <summary>Windowed-sinc filter with a 2-pixel window — the smallest, fastest, and softest of the Lanczos filters here.</summary>
    Lanczos2,

    /// <summary>Windowed-sinc filter with a 3-pixel window — a common general-purpose sharp resampler.</summary>
    Lanczos3,

    /// <summary>Windowed-sinc filter with a 5-pixel window — sharper than <see cref="Lanczos3"/>, more prone to ringing.</summary>
    Lanczos5,

    /// <summary>Windowed-sinc filter with an 8-pixel window — the sharpest and slowest of the Lanczos filters here.</summary>
    Lanczos8,

    /// <summary>The generalized cubic convolution filter with (B, C) = (1/3, 1/3), a widely-used balance between sharpness and ringing.</summary>
    MitchellNetravali,

    /// <summary>Copies the closest source pixel with no blending — the fastest filter, and the only one that never introduces new colors.</summary>
    NearestNeighbor,

    /// <summary>The generalized cubic convolution filter tuned to minimize both ringing and aliasing (B ≈ 0.3782, C ≈ 0.3109).</summary>
    Robidoux,

    /// <summary>A sharper variant of <see cref="Robidoux"/> (B ≈ 0.2620, C ≈ 0.3690).</summary>
    RobidouxSharp,

    /// <summary>The generalized cubic convolution filter with (B, C) = (1, 0) — a soft, uniform B-spline blur.</summary>
    Spline,

    /// <summary>Linear interpolation between the two nearest source pixels on each axis.</summary>
    Bilinear,

    /// <summary>Sinc windowed by the Welch (parabolic) window — a softer alternative to the Lanczos filters.</summary>
    Welch,
}
