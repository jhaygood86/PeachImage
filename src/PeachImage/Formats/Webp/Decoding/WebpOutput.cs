namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// Memory a WebP decode writes its finished pixel rows into directly: <paramref name="Pointer"/> to the first row, rows
/// <paramref name="Stride"/> bytes apart, for an image the caller expects to be <paramref name="Width"/> x <paramref name="Height"/>
/// in <paramref name="Format"/>. The caller keeps the memory pinned for the whole decode.
/// </summary>
internal readonly record struct WebpOutput(nint Pointer, int Stride, int Width, int Height, PixelFormat Format)
{
    /// <summary>The bitstream has to be the image the destination was sized for: the container and the bitstream header must agree before any row is written.</summary>
    public void Validate(int width, int height, PixelFormat format)
    {
        if (width != Width || height != Height || format != Format)
        {
            throw new WebpDecodingException(
                $"The WebP bitstream is {width}x{height} {format}, but the destination was sized for {Width}x{Height} {Format}.");
        }
    }

    /// <summary>The span for output row <paramref name="y"/>, <paramref name="rowBytes"/> long.</summary>
    public unsafe Span<byte> Row(int y, int rowBytes) => new((byte*)Pointer + ((long)y * Stride), rowBytes);
}
