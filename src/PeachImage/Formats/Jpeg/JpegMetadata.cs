namespace PeachImage.Formats.Jpeg;

/// <summary>JPEG-specific metadata facts that don't fit the generic <see cref="ImageMetadata"/> raw-profile model.</summary>
public sealed class JpegMetadata
{
    /// <summary>The color space the decoded frame's samples were encoded in.</summary>
    public required JpegColorSpace ColorSpace { get; init; }

    /// <summary>Whether the bitstream used progressive (as opposed to baseline sequential) scan encoding.</summary>
    public required bool IsProgressive { get; init; }
}
