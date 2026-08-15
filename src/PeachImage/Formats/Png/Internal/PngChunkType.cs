namespace PeachImage.Formats.Png.Internal;

/// <summary>
/// A PNG chunk's 4-byte ASCII type code, packed big-endian into a <see cref="uint"/> so it can be
/// compared/switched on cheaply. Case of each letter encodes chunk properties per spec §5.4.
/// </summary>
internal readonly record struct PngChunkType(uint Value)
{
    public static readonly PngChunkType Ihdr = FromAscii("IHDR");
    public static readonly PngChunkType Plte = FromAscii("PLTE");
    public static readonly PngChunkType Idat = FromAscii("IDAT");
    public static readonly PngChunkType Iend = FromAscii("IEND");
    public static readonly PngChunkType Trns = FromAscii("tRNS");
    public static readonly PngChunkType Gama = FromAscii("gAMA");
    public static readonly PngChunkType Chrm = FromAscii("cHRM");
    public static readonly PngChunkType Srgb = FromAscii("sRGB");
    public static readonly PngChunkType Iccp = FromAscii("iCCP");
    public static readonly PngChunkType Phys = FromAscii("pHYs");
    public static readonly PngChunkType Text = FromAscii("tEXt");
    public static readonly PngChunkType Ztxt = FromAscii("zTXt");
    public static readonly PngChunkType Itxt = FromAscii("iTXt");
    public static readonly PngChunkType Time = FromAscii("tIME");
    public static readonly PngChunkType Bkgd = FromAscii("bKGD");

    /// <summary>Whether this chunk must be understood by every conformant decoder (uppercase first letter).</summary>
    public bool IsCritical => (Value & 0x2000_0000) == 0;

    /// <summary>Whether this chunk may be safely skipped by a decoder that doesn't recognize it.</summary>
    public bool IsAncillary => !IsCritical;

    public static PngChunkType FromAscii(ReadOnlySpan<byte> bytes) =>
        new((uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]));

    private static PngChunkType FromAscii(string s) =>
        new((uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]));

    public override string ToString() => string.Create(4, Value, static (span, value) =>
    {
        span[0] = (char)((value >> 24) & 0xFF);
        span[1] = (char)((value >> 16) & 0xFF);
        span[2] = (char)((value >> 8) & 0xFF);
        span[3] = (char)(value & 0xFF);
    });
}
