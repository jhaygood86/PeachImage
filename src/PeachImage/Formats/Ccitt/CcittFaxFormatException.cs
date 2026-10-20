namespace PeachImage.Formats.Ccitt;

/// <summary>Base type for exceptions specific to the raw CCITT fax codec.</summary>
public class CcittFaxFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="CcittFaxFormatException"/>.</summary>
    public CcittFaxFormatException(string message)
        : base(message, "ccitt")
    {
    }

    /// <summary>Initializes a new instance of <see cref="CcittFaxFormatException"/> with an inner exception.</summary>
    public CcittFaxFormatException(string message, Exception innerException)
        : base(message, "ccitt", innerException)
    {
    }
}

/// <summary>Thrown when CCITT fax data is malformed or truncated, as opposed to well-formed but out of scope.</summary>
public sealed class CcittFaxDecodingException : CcittFaxFormatException
{
    /// <summary>Initializes a new instance of <see cref="CcittFaxDecodingException"/>.</summary>
    public CcittFaxDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="CcittFaxDecodingException"/> with an inner exception.</summary>
    public CcittFaxDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when CCITT fax data uses a feature this decoder does not implement (uncompressed mode).</summary>
public sealed class CcittFaxUnsupportedFeatureException : CcittFaxFormatException
{
    /// <summary>Initializes a new instance of <see cref="CcittFaxUnsupportedFeatureException"/>.</summary>
    public CcittFaxUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="CcittFaxUnsupportedFeatureException"/> with an inner exception.</summary>
    public CcittFaxUnsupportedFeatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
