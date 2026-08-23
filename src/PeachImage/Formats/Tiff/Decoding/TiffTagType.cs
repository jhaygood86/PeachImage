namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>TIFF 6.0 tag value types (spec §2, "Type"), used to size and interpret an IFD entry's value.</summary>
internal enum TiffTagType : ushort
{
    Byte = 1,
    Ascii = 2,
    Short = 3,
    Long = 4,
    Rational = 5,
    SByte = 6,
    Undefined = 7,
    SShort = 8,
    SLong = 9,
    SRational = 10,
    Float = 11,
    Double = 12,
}

/// <summary>Sizing helper for <see cref="TiffTagType"/>.</summary>
internal static class TiffTagTypeExtensions
{
    /// <summary>The size, in bytes, of a single value of this type. 0 for an unrecognized type — such a tag is skipped, not treated as fatal, since TIFF's tag space is far larger than anything this decoder needs to read.</summary>
    public static int GetByteSize(this TiffTagType type) => type switch
    {
        TiffTagType.Byte or TiffTagType.Ascii or TiffTagType.SByte or TiffTagType.Undefined => 1,
        TiffTagType.Short or TiffTagType.SShort => 2,
        TiffTagType.Long or TiffTagType.SLong or TiffTagType.Float => 4,
        TiffTagType.Rational or TiffTagType.SRational or TiffTagType.Double => 8,
        _ => 0,
    };
}
