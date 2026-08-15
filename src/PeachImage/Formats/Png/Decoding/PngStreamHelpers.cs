namespace PeachImage.Formats.Png.Decoding;

/// <summary>Small stream-reading helpers shared across PNG decode internals.</summary>
internal static class PngStreamHelpers
{
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
                throw new PngDecodingException("Unexpected end of stream while skipping a PNG chunk.");
            }

            remaining -= read;
        }
    }
}
