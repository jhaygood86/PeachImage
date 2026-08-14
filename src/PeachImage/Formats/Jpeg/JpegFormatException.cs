namespace PeachImage.Formats.Jpeg;

/// <summary>Base type for exceptions specific to the JPEG codec.</summary>
public class JpegFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="JpegFormatException"/>.</summary>
    public JpegFormatException(string message)
        : base(message, "jpeg")
    {
    }

    /// <summary>Initializes a new instance of <see cref="JpegFormatException"/> with an inner exception.</summary>
    public JpegFormatException(string message, Exception innerException)
        : base(message, "jpeg", innerException)
    {
    }
}

/// <summary>Thrown when a JPEG bitstream cannot be decoded, either because it is malformed or uses an unsupported feature.</summary>
public sealed class JpegDecodingException : JpegFormatException
{
    /// <summary>Initializes a new instance of <see cref="JpegDecodingException"/>.</summary>
    public JpegDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="JpegDecodingException"/> with an inner exception.</summary>
    public JpegDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an <see cref="Image"/> cannot be encoded as JPEG.</summary>
public sealed class JpegEncodingException : JpegFormatException
{
    /// <summary>Initializes a new instance of <see cref="JpegEncodingException"/>.</summary>
    public JpegEncodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="JpegEncodingException"/> with an inner exception.</summary>
    public JpegEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
