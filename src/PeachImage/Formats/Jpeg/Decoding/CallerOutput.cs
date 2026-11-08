namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Memory the caller owns, pinned for the length of the decode, that a streaming decode writes the finished image rows into
/// instead of renting a buffer of its own. Rows are <see cref="Stride"/> bytes apart.
/// </summary>
internal sealed class CallerOutput
{
    public CallerOutput(nint pointer, int stride)
    {
        Pointer = pointer;
        Stride = stride;
    }

    /// <summary>The first byte of the first row.</summary>
    public nint Pointer { get; }

    /// <summary>The distance in bytes between the starts of consecutive rows.</summary>
    public int Stride { get; }

    /// <summary>Set once a streaming decode has filled the whole image in the color space it assumed; false means the memory is not usable.</summary>
    public bool Written { get; set; }
}
