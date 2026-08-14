namespace PeachImage.Formats.Jpeg.Markers.Segments;

/// <summary>A single component's entry within a scan header (SOS).</summary>
internal sealed class JpegScanComponent
{
    /// <summary>The frame component id this scan component refers to.</summary>
    public required byte ComponentSelector { get; init; }

    /// <summary>The DC Huffman table id to use for this component in this scan.</summary>
    public required byte DcTableSelector { get; init; }

    /// <summary>The AC Huffman table id to use for this component in this scan.</summary>
    public required byte AcTableSelector { get; init; }
}

/// <summary>A parsed JPEG scan header (SOS payload, excluding the entropy-coded data that follows it).</summary>
internal sealed class JpegScanHeader
{
    /// <summary>The components participating in this scan, in scan order.</summary>
    public required JpegScanComponent[] Components { get; init; }

    /// <summary>Spectral selection start (Ss). 0 for baseline and progressive DC scans.</summary>
    public required int SpectralStart { get; init; }

    /// <summary>Spectral selection end (Se). 63 for baseline; 0 for progressive DC scans.</summary>
    public required int SpectralEnd { get; init; }

    /// <summary>Successive approximation bit position high (Ah). 0 on a coefficient's first scan.</summary>
    public required int SuccessiveApproximationHigh { get; init; }

    /// <summary>Successive approximation bit position low (Al).</summary>
    public required int SuccessiveApproximationLow { get; init; }

    /// <summary>Parses a scan header payload (already stripped of the marker and length field).</summary>
    public static JpegScanHeader Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new JpegDecodingException("Truncated JPEG scan header.");
        }

        int componentCount = payload[0];
        int expectedLength = 1 + (componentCount * 2) + 3;
        if (componentCount is < 1 or > 4 || payload.Length < expectedLength)
        {
            throw new JpegDecodingException("Invalid or truncated JPEG scan header.");
        }

        var components = new JpegScanComponent[componentCount];
        int offset = 1;
        for (int i = 0; i < componentCount; i++)
        {
            byte selector = payload[offset];
            byte tables = payload[offset + 1];
            components[i] = new JpegScanComponent
            {
                ComponentSelector = selector,
                DcTableSelector = (byte)(tables >> 4),
                AcTableSelector = (byte)(tables & 0x0F),
            };
            offset += 2;
        }

        byte ss = payload[offset];
        byte se = payload[offset + 1];
        byte ahAl = payload[offset + 2];

        if (ss > 63 || se > 63 || ss > se)
        {
            throw new JpegDecodingException($"Invalid JPEG scan header: spectral selection Ss={ss}, Se={se} is out of the valid 0-63 range.");
        }

        return new JpegScanHeader
        {
            Components = components,
            SpectralStart = ss,
            SpectralEnd = se,
            SuccessiveApproximationHigh = ahAl >> 4,
            SuccessiveApproximationLow = ahAl & 0x0F,
        };
    }
}
