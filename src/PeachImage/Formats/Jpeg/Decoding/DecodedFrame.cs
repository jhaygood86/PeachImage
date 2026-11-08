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

    /// <summary>
    /// When the frame was decoded in streaming mode, each component's already inverse-transformed sample plane
    /// (<c>ActualWidthInSamples x ActualHeightInSamples</c>, pooled), in the same order as <see cref="Components"/>; the
    /// coefficient buffers then no longer hold the frame. <see langword="null"/> for a frame that kept its coefficients.
    /// </summary>
    public byte[][]? StreamedPlanes { get; set; }

    /// <summary>Returns the pooled coefficient buffers and any streamed planes. Call once the frame has been reconstructed.</summary>
    public void ReleaseBuffers()
    {
        foreach (var component in Components)
        {
            component.Coefficients.Return();
        }

        if (StreamedPlanes is { } planes)
        {
            foreach (var plane in planes)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(plane);
            }

            StreamedPlanes = null;
        }
    }
}
