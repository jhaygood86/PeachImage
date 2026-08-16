namespace PeachImage.Formats.Gif;

/// <summary>Base type for exceptions specific to the GIF codec.</summary>
public class GifFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="GifFormatException"/>.</summary>
    public GifFormatException(string message)
        : base(message, "gif")
    {
    }

    /// <summary>Initializes a new instance of <see cref="GifFormatException"/> with an inner exception.</summary>
    public GifFormatException(string message, Exception innerException)
        : base(message, "gif", innerException)
    {
    }
}

/// <summary>Thrown when a GIF bitstream cannot be decoded, either because it is malformed or uses an unsupported feature.</summary>
public sealed class GifDecodingException : GifFormatException
{
    /// <summary>Initializes a new instance of <see cref="GifDecodingException"/>.</summary>
    public GifDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="GifDecodingException"/> with an inner exception.</summary>
    public GifDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an <see cref="Image"/> or <see cref="AnimatedImage"/> cannot be encoded as GIF.</summary>
public sealed class GifEncodingException : GifFormatException
{
    /// <summary>Initializes a new instance of <see cref="GifEncodingException"/>.</summary>
    public GifEncodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="GifEncodingException"/> with an inner exception.</summary>
    public GifEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
