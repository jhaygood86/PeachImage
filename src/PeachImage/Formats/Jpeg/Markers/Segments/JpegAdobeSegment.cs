namespace PeachImage.Formats.Jpeg.Markers.Segments;

/// <summary>The Adobe APP14 marker segment, which disambiguates color transform semantics for 3- and 4-component JPEGs.</summary>
internal readonly struct JpegAdobeSegment(byte transform, ushort dctEncodeVersion = 0, ushort flags0 = 0, ushort flags1 = 0)
{
    /// <summary>The encoder's DCTEncodeVersion field (typically 0x0064 / 100).</summary>
    public ushort DctEncodeVersion { get; } = dctEncodeVersion;

    /// <summary>The first Adobe-defined flags word (encoder hints; not consulted for color-space resolution).</summary>
    public ushort Flags0 { get; } = flags0;

    /// <summary>The second Adobe-defined flags word (reserved; not consulted for color-space resolution).</summary>
    public ushort Flags1 { get; } = flags1;

    /// <summary>0 = unknown (CMYK or direct RGB), 1 = YCbCr, 2 = YCCK.</summary>
    public byte Transform { get; } = transform;

    /// <summary>Attempts to parse an APP14 payload as an Adobe segment; returns <see langword="false"/> if the signature doesn't match.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out JpegAdobeSegment segment)
    {
        ReadOnlySpan<byte> signature = "Adobe"u8;
        if (payload.Length >= 12 && payload[..5].SequenceEqual(signature))
        {
            ushort dctEncodeVersion = (ushort)((payload[5] << 8) | payload[6]);
            ushort flags0 = (ushort)((payload[7] << 8) | payload[8]);
            ushort flags1 = (ushort)((payload[9] << 8) | payload[10]);
            segment = new JpegAdobeSegment(payload[11], dctEncodeVersion, flags0, flags1);
            return true;
        }

        segment = default;
        return false;
    }
}
