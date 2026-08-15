namespace PeachImage.Formats.Webp.Decoding;

/// <summary>Small stream-reading helpers shared across WebP decode internals.</summary>
internal static class WebpStreamHelpers
{
    /// <summary>Reads exactly <paramref name="buffer"/>'s length of bytes, converting a short read into <see cref="WebpDecodingException"/> rather than letting a raw I/O exception escape.</summary>
    public static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            throw new WebpDecodingException("Unexpected end of stream while reading WebP data.", ex);
        }
    }

    /// <summary>Advances the stream by <paramref name="byteCount"/> bytes, seeking when possible and discarding via reads otherwise (for non-seekable streams).</summary>
    public static void SkipForward(Stream stream, long byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(byteCount, SeekOrigin.Current);
            return;
        }

        Span<byte> buffer = stackalloc byte[4096];
        long remaining = byteCount;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer[..chunk]);
            if (read == 0)
            {
                throw new WebpDecodingException("Unexpected end of stream while skipping WebP chunk data.");
            }

            remaining -= read;
        }
    }
}
