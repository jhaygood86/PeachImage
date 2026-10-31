namespace PeachImage;

/// <summary>
/// Thrown when an image would exceed a limit the caller set in <see cref="DecoderOptions"/> (<see cref="DecoderOptions.MaxPixels"/>
/// or <see cref="DecoderOptions.MaxExpansionRatio"/>), before the memory for it is allocated or as soon as the limit is crossed.
/// </summary>
public sealed class ImageTooLargeException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="ImageTooLargeException"/> with the given message.</summary>
    public ImageTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="ImageTooLargeException"/> with the given message and format name.</summary>
    public ImageTooLargeException(string message, string? formatName)
        : base(message, formatName)
    {
    }
}
