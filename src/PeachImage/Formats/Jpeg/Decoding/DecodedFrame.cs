using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>The fully-decoded (but not yet reconstructed into pixels) result of parsing a JPEG bitstream.</summary>
internal sealed class DecodedFrame
{
    /// <summary>The frame header (dimensions, precision, per-component sampling factors).</summary>
    public required JpegFrameHeader FrameHeader { get; init; }

    /// <summary>Per-component decode state, including the fully-populated coefficient buffers.</summary>
    public required ComponentDecodeState[] Components { get; init; }

    /// <summary>The resolved color space samples are encoded in.</summary>
    public required JpegColorSpace ColorSpace { get; init; }

    /// <summary>Whether direct (non-YCCK) 4-component samples follow Adobe's inverted-CMYK convention.</summary>
    public required bool IsAdobeInverted { get; init; }

    /// <summary>Raw metadata profiles captured while parsing (JFIF/EXIF/ICC/Adobe/etc.).</summary>
    public required List<RawMetadataProfile> Metadata { get; init; }
}
