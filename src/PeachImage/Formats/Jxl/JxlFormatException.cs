namespace PeachImage.Formats.Jxl;

/// <summary>Base type for exceptions specific to the JPEG XL codec.</summary>
public class JxlFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="JxlFormatException"/>.</summary>
    public JxlFormatException(string message)
        : base(message, "jxl")
    {
    }

    /// <summary>Initializes a new instance of <see cref="JxlFormatException"/> with an inner exception.</summary>
    public JxlFormatException(string message, Exception innerException)
        : base(message, "jxl", innerException)
    {
    }
}

/// <summary>Thrown when a JPEG XL file is malformed or truncated, as opposed to well-formed but out of scope.</summary>
public sealed class JxlDecodingException : JxlFormatException
{
    /// <summary>Initializes a new instance of <see cref="JxlDecodingException"/>.</summary>
    public JxlDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="JxlDecodingException"/> with an inner exception.</summary>
    public JxlDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a JPEG XL file is well-formed but uses a feature this decoder does not implement (yet), as
/// opposed to <see cref="JxlDecodingException"/>, which means the file itself is malformed. A sibling rather
/// than a subclass so corpus tests can tell "skip it" from "real decoding bug".
/// </summary>
public sealed class JxlUnsupportedFeatureException : JxlFormatException
{
    /// <summary>Initializes a new instance of <see cref="JxlUnsupportedFeatureException"/>.</summary>
    public JxlUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="JxlUnsupportedFeatureException"/> with an inner exception.</summary>
    public JxlUnsupportedFeatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
