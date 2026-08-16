namespace PeachImage.Formats.Webp;

/// <summary>Base type for exceptions specific to the WebP codec.</summary>
public class WebpFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="WebpFormatException"/>.</summary>
    public WebpFormatException(string message)
        : base(message, "webp")
    {
    }

    /// <summary>Initializes a new instance of <see cref="WebpFormatException"/> with an inner exception.</summary>
    public WebpFormatException(string message, Exception innerException)
        : base(message, "webp", innerException)
    {
    }
}

/// <summary>Thrown when a WebP bitstream cannot be decoded, either because it is malformed or uses an unsupported feature.</summary>
public sealed class WebpDecodingException : WebpFormatException
{
    /// <summary>Initializes a new instance of <see cref="WebpDecodingException"/>.</summary>
    public WebpDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="WebpDecodingException"/> with an inner exception.</summary>
    public WebpDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an <see cref="Image"/> cannot be encoded as WebP, either because its pixel format is unsupported or its dimensions exceed what VP8L can represent.</summary>
public sealed class WebpEncodingException : WebpFormatException
{
    /// <summary>Initializes a new instance of <see cref="WebpEncodingException"/>.</summary>
    public WebpEncodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="WebpEncodingException"/> with an inner exception.</summary>
    public WebpEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
