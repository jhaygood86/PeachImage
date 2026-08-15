namespace PeachImage.Formats.Bmp;

/// <summary>Base type for exceptions specific to the BMP codec.</summary>
public class BmpFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="BmpFormatException"/>.</summary>
    public BmpFormatException(string message)
        : base(message, "bmp")
    {
    }

    /// <summary>Initializes a new instance of <see cref="BmpFormatException"/> with an inner exception.</summary>
    public BmpFormatException(string message, Exception innerException)
        : base(message, "bmp", innerException)
    {
    }
}

/// <summary>Thrown when a BMP bitstream cannot be decoded, either because it is malformed or uses an unsupported feature.</summary>
public sealed class BmpDecodingException : BmpFormatException
{
    /// <summary>Initializes a new instance of <see cref="BmpDecodingException"/>.</summary>
    public BmpDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="BmpDecodingException"/> with an inner exception.</summary>
    public BmpDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an <see cref="Image"/> cannot be encoded as BMP.</summary>
public sealed class BmpEncodingException : BmpFormatException
{
    /// <summary>Initializes a new instance of <see cref="BmpEncodingException"/>.</summary>
    public BmpEncodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="BmpEncodingException"/> with an inner exception.</summary>
    public BmpEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
