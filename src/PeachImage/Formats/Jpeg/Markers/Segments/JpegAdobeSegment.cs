namespace PeachImage.Formats.Jpeg.Markers.Segments;

/// <summary>The Adobe APP14 marker segment, which disambiguates color transform semantics for 3- and 4-component JPEGs.</summary>
internal readonly struct JpegAdobeSegment(byte transform)
{
    /// <summary>0 = unknown (CMYK or direct RGB), 1 = YCbCr, 2 = YCCK.</summary>
    public byte Transform { get; } = transform;

    /// <summary>Attempts to parse an APP14 payload as an Adobe segment; returns <see langword="false"/> if the signature doesn't match.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out JpegAdobeSegment segment)
    {
        ReadOnlySpan<byte> signature = "Adobe"u8;
        if (payload.Length >= 12 && payload[..5].SequenceEqual(signature))
        {
            segment = new JpegAdobeSegment(payload[11]);
            return true;
        }

        segment = default;
        return false;
    }
}
