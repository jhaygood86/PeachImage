namespace PeachImage;

/// <summary>
/// Thrown when <see cref="IccColorProfile"/> is given bytes that are too short, malformed, or resolve to a
/// device color space or transform PeachImage's ICC engine doesn't support. Not derived from
/// <see cref="ImageFormatException"/> — an ICC profile isn't an image container format, and can be used
/// independent of any decoded <see cref="Image"/> (e.g. loaded from a standalone <c>.icc</c>/<c>.icm</c> file).
/// </summary>
public sealed class IccProfileException : Exception
{
    /// <summary>Initializes a new instance of <see cref="IccProfileException"/> with the given message.</summary>
    public IccProfileException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="IccProfileException"/> with the given message and inner exception.</summary>
    public IccProfileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
