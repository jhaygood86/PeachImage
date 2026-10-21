namespace PeachImage.Formats.Jpeg2000;

/// <summary>Base type for exceptions specific to the JPEG 2000 codec.</summary>
public class Jpeg2000FormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="Jpeg2000FormatException"/>.</summary>
    public Jpeg2000FormatException(string message)
        : base(message, "jpeg2000")
    {
    }

    /// <summary>Initializes a new instance of <see cref="Jpeg2000FormatException"/> with an inner exception.</summary>
    public Jpeg2000FormatException(string message, Exception innerException)
        : base(message, "jpeg2000", innerException)
    {
    }
}

/// <summary>Thrown when a JPEG 2000 file is malformed or truncated, as opposed to well-formed but out of scope.</summary>
public sealed class Jpeg2000DecodingException : Jpeg2000FormatException
{
    /// <summary>Initializes a new instance of <see cref="Jpeg2000DecodingException"/>.</summary>
    public Jpeg2000DecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="Jpeg2000DecodingException"/> with an inner exception.</summary>
    public Jpeg2000DecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a JPEG 2000 file uses a feature this decoder does not implement (Part 2 extensions, sample depths above 16 bits, ...).</summary>
public sealed class Jpeg2000UnsupportedFeatureException : Jpeg2000FormatException
{
    /// <summary>Initializes a new instance of <see cref="Jpeg2000UnsupportedFeatureException"/>.</summary>
    public Jpeg2000UnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="Jpeg2000UnsupportedFeatureException"/> with an inner exception.</summary>
    public Jpeg2000UnsupportedFeatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
