namespace PeachImage;

/// <summary>Base type for all exceptions PeachImage throws while identifying, decoding, or encoding images.</summary>
public class ImageFormatException : Exception
{
    /// <summary>The name of the format involved, if known.</summary>
    public string? FormatName { get; }

    /// <summary>Initializes a new instance of <see cref="ImageFormatException"/>.</summary>
    public ImageFormatException()
    {
    }

    /// <summary>Initializes a new instance of <see cref="ImageFormatException"/> with the given message.</summary>
    public ImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="ImageFormatException"/> with the given message and format name.</summary>
    public ImageFormatException(string message, string? formatName)
        : base(message) => FormatName = formatName;

    /// <summary>Initializes a new instance of <see cref="ImageFormatException"/> with the given message and inner exception.</summary>
    public ImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of <see cref="ImageFormatException"/> with the given message, format name, and inner exception.</summary>
    public ImageFormatException(string message, string? formatName, Exception innerException)
        : base(message, innerException) => FormatName = formatName;
}
