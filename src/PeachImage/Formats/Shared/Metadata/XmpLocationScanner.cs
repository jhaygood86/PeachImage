namespace PeachImage.Formats.Shared.Metadata;

/// <summary>Reports whether an XMP packet names a GPS position, by a case-sensitive text scan (no XML parse).</summary>
internal static class XmpLocationScanner
{
    /// <summary>The most bytes of a packet that are scanned; the rest is ignored.</summary>
    public const int MaxScannedBytes = 1 << 20;

    private static ReadOnlySpan<byte> Latitude => "GPSLatitude"u8;

    private static ReadOnlySpan<byte> Longitude => "GPSLongitude"u8;

    /// <summary>True when the first <see cref="MaxScannedBytes"/> bytes of <paramref name="packet"/> contain <c>GPSLatitude</c> or <c>GPSLongitude</c>.</summary>
    public static bool NamesLocation(ReadOnlySpan<byte> packet)
    {
        if (packet.Length > MaxScannedBytes)
        {
            packet = packet[..MaxScannedBytes];
        }

        return packet.IndexOf(Latitude) >= 0 || packet.IndexOf(Longitude) >= 0;
    }
}
