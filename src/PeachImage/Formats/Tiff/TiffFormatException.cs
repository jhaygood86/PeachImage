namespace PeachImage.Formats.Tiff;

/// <summary>Base type for exceptions specific to the TIFF codec.</summary>
public class TiffFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="TiffFormatException"/>.</summary>
    public TiffFormatException(string message)
        : base(message, "tiff")
    {
    }

    /// <summary>Initializes a new instance of <see cref="TiffFormatException"/> with an inner exception.</summary>
    public TiffFormatException(string message, Exception innerException)
        : base(message, "tiff", innerException)
    {
    }
}

/// <summary>Thrown when a TIFF file is malformed or truncated, as opposed to well-formed but out of scope.</summary>
public sealed class TiffDecodingException : TiffFormatException
{
    /// <summary>Initializes a new instance of <see cref="TiffDecodingException"/>.</summary>
    public TiffDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="TiffDecodingException"/> with an inner exception.</summary>
    public TiffDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a TIFF file uses a feature deliberately out of scope for this decoder (tiled organization,
/// planar/separate PlanarConfiguration, compression other than none/LZW/PackBits, BigTIFF, floating-point or
/// signed SampleFormat, unsupported bit depths, or photometric interpretations outside
/// WhiteIsZero/BlackIsZero/RGB/Palette/Separated) rather than one that is malformed. Kept as a sibling of
/// <see cref="TiffDecodingException"/> (not a subclass) so corpus/differential tests can distinguish
/// "deliberately unsupported, skip it" from "a real decoding bug" by catching this narrower type first.
/// </summary>
public sealed class TiffUnsupportedFeatureException : TiffFormatException
{
    /// <summary>Initializes a new instance of <see cref="TiffUnsupportedFeatureException"/>.</summary>
    public TiffUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="TiffUnsupportedFeatureException"/> with an inner exception.</summary>
    public TiffUnsupportedFeatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
