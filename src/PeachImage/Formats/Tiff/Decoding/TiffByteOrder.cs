namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>The byte order a TIFF file declares via its 2-byte 'II'/'MM' mark, governing every multi-byte field in the file, including 16-bit sample values.</summary>
internal enum TiffByteOrder
{
    /// <summary>'II' — little-endian.</summary>
    LittleEndian,

    /// <summary>'MM' — big-endian.</summary>
    BigEndian,
}
