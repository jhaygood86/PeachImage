namespace PeachImage.Formats.Jbig2;

/// <summary>Base type for exceptions specific to the JBIG2 codec.</summary>
public class Jbig2FormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="Jbig2FormatException"/>.</summary>
    public Jbig2FormatException(string message)
        : base(message, "jbig2")
    {
    }

    /// <summary>Initializes a new instance of <see cref="Jbig2FormatException"/> with an inner exception.</summary>
    public Jbig2FormatException(string message, Exception innerException)
        : base(message, "jbig2", innerException)
    {
    }
}

/// <summary>Thrown when a JBIG2 stream is malformed or truncated, as opposed to well-formed but out of scope.</summary>
public sealed class Jbig2DecodingException : Jbig2FormatException
{
    /// <summary>Initializes a new instance of <see cref="Jbig2DecodingException"/>.</summary>
    public Jbig2DecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="Jbig2DecodingException"/> with an inner exception.</summary>
    public Jbig2DecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a JBIG2 stream uses a feature this decoder does not implement (the extended 12-pixel generic template, colour extensions).</summary>
public sealed class Jbig2UnsupportedFeatureException : Jbig2FormatException
{
    /// <summary>Initializes a new instance of <see cref="Jbig2UnsupportedFeatureException"/>.</summary>
    public Jbig2UnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="Jbig2UnsupportedFeatureException"/> with an inner exception.</summary>
    public Jbig2UnsupportedFeatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
