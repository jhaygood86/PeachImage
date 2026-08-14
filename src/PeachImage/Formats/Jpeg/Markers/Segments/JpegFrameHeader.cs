namespace PeachImage.Formats.Jpeg.Markers.Segments;

/// <summary>A single component's entry within a JPEG frame header (SOFn).</summary>
internal sealed class JpegFrameComponent
{
    /// <summary>The component identifier, as referenced by scan headers.</summary>
    public required byte Id { get; init; }

    /// <summary>Horizontal sampling factor (1-4).</summary>
    public required int HorizontalSamplingFactor { get; init; }

    /// <summary>Vertical sampling factor (1-4).</summary>
    public required int VerticalSamplingFactor { get; init; }

    /// <summary>The quantization table id this component dequantizes with.</summary>
    public required byte QuantizationTableId { get; init; }
}

/// <summary>A parsed JPEG frame header (SOF0/SOF2 payload).</summary>
internal sealed class JpegFrameHeader
{
    /// <summary>Sample precision in bits (only 8 is supported).</summary>
    public required int Precision { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Frame width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>The frame's components, in the order they appeared in the header.</summary>
    public required JpegFrameComponent[] Components { get; init; }

    /// <summary>Whether this frame uses progressive (SOF2) rather than baseline sequential (SOF0) encoding.</summary>
    public required bool IsProgressive { get; init; }

    /// <summary>Parses a frame header payload (already stripped of the marker and length field).</summary>
    public static JpegFrameHeader Parse(ReadOnlySpan<byte> payload, bool isProgressive)
    {
        if (payload.Length < 6)
        {
            throw new JpegDecodingException("Truncated JPEG frame header.");
        }

        int precision = payload[0];
        int height = (payload[1] << 8) | payload[2];
        int width = (payload[3] << 8) | payload[4];
        int componentCount = payload[5];

        if (precision != 8)
        {
            throw new JpegDecodingException($"Unsupported JPEG sample precision: {precision} bits. Only 8-bit precision is supported.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new JpegDecodingException($"Invalid JPEG frame dimensions: {width}x{height}.");
        }

        if (componentCount is not (1 or 3 or 4))
        {
            throw new JpegDecodingException($"Unsupported JPEG component count: {componentCount}.");
        }

        int expectedLength = 6 + (componentCount * 3);
        if (payload.Length < expectedLength)
        {
            throw new JpegDecodingException("Truncated JPEG frame header component list.");
        }

        var components = new JpegFrameComponent[componentCount];
        int offset = 6;
        for (int i = 0; i < componentCount; i++)
        {
            byte id = payload[offset];
            byte sampling = payload[offset + 1];
            byte quantId = payload[offset + 2];

            int h = sampling >> 4;
            int v = sampling & 0x0F;
            if (h is < 1 or > 4 || v is < 1 or > 4)
            {
                throw new JpegDecodingException($"Invalid JPEG component sampling factors: {h}x{v}.");
            }

            components[i] = new JpegFrameComponent
            {
                Id = id,
                HorizontalSamplingFactor = h,
                VerticalSamplingFactor = v,
                QuantizationTableId = quantId,
            };
            offset += 3;
        }

        return new JpegFrameHeader
        {
            Precision = precision,
            Height = height,
            Width = width,
            Components = components,
            IsProgressive = isProgressive,
        };
    }
}
