namespace PeachImage;

/// <summary>Identifies the in-memory layout of a decoded <see cref="Image"/>'s pixel buffer.</summary>
public enum PixelFormat : byte
{
    /// <summary>Single-channel 8-bit grayscale.</summary>
    Gray8,

    /// <summary>Three-channel 8-bit red/green/blue, tightly interleaved.</summary>
    Rgb24,

    /// <summary>Four-channel 8-bit red/green/blue/alpha, tightly interleaved.</summary>
    Rgba32,

    /// <summary>Four-channel 8-bit cyan/magenta/yellow/key (black), tightly interleaved.</summary>
    Cmyk32,
}
