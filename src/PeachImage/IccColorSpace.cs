namespace PeachImage;

/// <summary>The device color space an <see cref="IccColorProfile"/> declares its transform expects.</summary>
public enum IccColorSpace
{
    /// <summary>The profile's device color space isn't one PeachImage's ICC engine can convert (e.g. Lab, XYZ, or a multi-ink space beyond CMYK).</summary>
    Unknown,

    /// <summary>Single-channel grayscale.</summary>
    Gray,

    /// <summary>Three-channel red/green/blue.</summary>
    Rgb,

    /// <summary>Four-channel cyan/magenta/yellow/key (black).</summary>
    Cmyk,
}
