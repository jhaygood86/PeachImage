namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>The parsed 8-byte TIFF header: byte order plus the absolute offset of the first IFD.</summary>
internal readonly record struct TiffHeader(TiffByteOrder ByteOrder, uint FirstIfdOffset);
