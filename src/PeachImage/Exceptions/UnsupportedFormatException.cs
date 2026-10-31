namespace PeachImage;

/// <summary>
/// Thrown when an image is in a format PeachImage recognises but the caller's <see cref="DecoderOptions.AllowedFormats"/>
/// does not permit. It is thrown right after the format is recognised from the file's signature and before any decoder
/// for that format runs.
/// </summary>
public sealed class UnsupportedFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="UnsupportedFormatException"/> for the format named <paramref name="formatName"/>.</summary>
    public UnsupportedFormatException(string formatName)
        : base($"The '{formatName}' format is not in the allowed set of formats.", formatName)
    {
    }

    /// <summary>Initializes a new instance of <see cref="UnsupportedFormatException"/> with the given message and format name.</summary>
    public UnsupportedFormatException(string message, string? formatName)
        : base(message, formatName)
    {
    }
}
