namespace PeachImage;

/// <summary>Thrown when no registered codec recognizes an image's format.</summary>
public sealed class UnknownImageFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="UnknownImageFormatException"/>.</summary>
    public UnknownImageFormatException()
        : base("The image format could not be determined.")
    {
    }

    /// <summary>Initializes a new instance of <see cref="UnknownImageFormatException"/> with the given message.</summary>
    public UnknownImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="UnknownImageFormatException"/> with the given message and format name.</summary>
    public UnknownImageFormatException(string message, string? formatName)
        : base(message, formatName)
    {
    }
}
