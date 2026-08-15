namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>The file-header and DIB-header fields needed to decode a BMP's pixel data, normalized across every recognized header variant (OS/2 1.x/2.x, BITMAPINFOHEADER through BITMAPV5HEADER).</summary>
internal readonly record struct BmpHeader
{
    /// <summary>Absolute byte offset of the pixel data, from the start of the file.</summary>
    public uint PixelDataOffset { get; init; }

    /// <summary>
    /// Total bytes consumed from the stream to read the file header, DIB header, and (for the classic
    /// 40-byte-header-plus-trailing-bitfields quirk) any extra mask DWORDs. Tracked explicitly, rather than
    /// relying on <see cref="Stream.Position"/>, because BMP decoding must also work against non-seekable
    /// streams (see <c>PrefixedStream</c>).
    /// </summary>
    public uint HeaderBytesConsumed { get; init; }

    /// <summary>The DIB header's own declared size, in bytes. Determines which header variant was parsed.</summary>
    public uint DibHeaderSize { get; init; }

    /// <summary>Image width, in pixels. Always positive.</summary>
    public int Width { get; init; }

    /// <summary>Image height, in pixels. Always positive — sign is already resolved into <see cref="TopDown"/>.</summary>
    public int Height { get; init; }

    /// <summary>Whether rows are stored top-to-bottom (negative declared height) rather than the BMP default of bottom-to-top.</summary>
    public bool TopDown { get; init; }

    /// <summary>Bits per pixel: 1, 4, 8, 16, 24, or 32.</summary>
    public int BitCount { get; init; }

    public BmpCompression Compression { get; init; }

    /// <summary>Declared palette entry count. 0 means "use the default for <see cref="BitCount"/>" (1 &lt;&lt; BitCount).</summary>
    public uint ColorsUsed { get; init; }

    /// <summary>Declared compressed/pixel data size in bytes, or 0 if unreliable/absent (common for BI_RGB).</summary>
    public uint DeclaredImageSize { get; init; }

    public uint RMask { get; init; }

    public uint GMask { get; init; }

    public uint BMask { get; init; }

    public uint AMask { get; init; }

    /// <summary>Whether a genuine, spec-declared alpha mask is present (non-zero <see cref="AMask"/> under BI_BITFIELDS/BI_ALPHABITFIELDS or a V3+ header).</summary>
    public bool HasAlphaMask { get; init; }

    /// <summary>Bytes per palette entry: 3 for BITMAPCOREHEADER's RGBTRIPLE, 4 for RGBQUAD.</summary>
    public int PaletteEntrySize { get; init; }
}
