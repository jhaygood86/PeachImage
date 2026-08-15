namespace PeachImage.Formats.Png;

/// <summary>How the PNG encoder chooses a per-scanline filter type.</summary>
public enum PngFilterStrategy
{
    /// <summary>Computes all 5 filter candidates per row and picks the one with the lowest minimum-sum-of-absolute-differences score (libpng's standard heuristic). Best compression for photographic content.</summary>
    Adaptive,

    /// <summary>Always emit filter type None (no filtering). Fastest; best for content that doesn't benefit from filtering (e.g. already-random/noisy data).</summary>
    FixedNone,

    /// <summary>Always emit filter type Sub.</summary>
    FixedSub,

    /// <summary>Always emit filter type Up.</summary>
    FixedUp,

    /// <summary>Always emit filter type Average.</summary>
    FixedAverage,

    /// <summary>Always emit filter type Paeth. Often a strong fixed choice for flat/synthetic graphics.</summary>
    FixedPaeth,
}
