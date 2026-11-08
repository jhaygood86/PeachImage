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

    /// <summary>
    /// For a streaming decode that also converted rows as they became available: the finished image bytes (Gray8 for
    /// <see cref="JpegColorSpace.Grayscale"/>, Rgb24 for <see cref="JpegColorSpace.YCbCr"/>), pooled. Reconstruction takes
    /// ownership of it when it uses it. <see langword="null"/> otherwise.
    /// </summary>
    public byte[]? StreamedOutput { get; set; }

    /// <summary>The color space <see cref="StreamedOutput"/> was converted as; it is only usable if the frame resolves to the same one.</summary>
    public JpegColorSpace StreamedOutputColorSpace { get; set; }

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
                // A grayscale plane doubling as the finished image belongs to the image pool, and goes back with StreamedOutput below.
                if (!ReferenceEquals(plane, StreamedOutput))
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(plane);
                }
            }

            StreamedPlanes = null;
        }

        if (StreamedOutput is { } output)
        {
            PeachImage.Internal.ImageBufferPool.Shared.Return(output);
            StreamedOutput = null;
        }
    }
}
