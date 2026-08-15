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
