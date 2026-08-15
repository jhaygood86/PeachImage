namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>The BMP pixel-data compression scheme, normalized from both the Windows and OS/2 numeric code spaces.</summary>
internal enum BmpCompression
{
    /// <summary>Uncompressed. Windows/OS/2 code 0.</summary>
    Rgb,

    /// <summary>8-bit run-length encoding. Windows/OS/2 code 1.</summary>
    Rle8,

    /// <summary>4-bit run-length encoding. Windows/OS/2 code 2.</summary>
    Rle4,

    /// <summary>Uncompressed with explicit R/G/B channel masks. Windows code 3.</summary>
    Bitfields,

    /// <summary>Embedded JPEG payload. Windows code 4. Not decoded — treated as unsupported.</summary>
    Jpeg,

    /// <summary>Embedded PNG payload. Windows code 5. Not decoded — treated as unsupported.</summary>
    Png,

    /// <summary>Uncompressed with explicit R/G/B/A channel masks (Windows CE). Windows code 6.</summary>
    AlphaBitfields,

    /// <summary>OS/2 1-bit Huffman encoding or 24-bit RLE — not implemented, always rejected.</summary>
    Unsupported,
}
