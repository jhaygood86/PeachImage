namespace PeachImage.Formats.Png;

/// <summary>Base type for exceptions specific to the PNG codec.</summary>
public class PngFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="PngFormatException"/>.</summary>
    public PngFormatException(string message)
        : base(message, "png")
    {
    }

    /// <summary>Initializes a new instance of <see cref="PngFormatException"/> with an inner exception.</summary>
    public PngFormatException(string message, Exception innerException)
        : base(message, "png", innerException)
    {
    }
}

/// <summary>Thrown when a PNG bitstream cannot be decoded, either because it is malformed or uses an unsupported feature.</summary>
public sealed class PngDecodingException : PngFormatException
{
    /// <summary>Initializes a new instance of <see cref="PngDecodingException"/>.</summary>
    public PngDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="PngDecodingException"/> with an inner exception.</summary>
    public PngDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an <see cref="Image"/> cannot be encoded as PNG.</summary>
public sealed class PngEncodingException : PngFormatException
{
    /// <summary>Initializes a new instance of <see cref="PngEncodingException"/>.</summary>
    public PngEncodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="PngEncodingException"/> with an inner exception.</summary>
    public PngEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
