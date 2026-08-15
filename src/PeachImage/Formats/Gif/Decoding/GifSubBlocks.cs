using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reads/skips a run of size-prefixed GIF data sub-blocks (each up to 255 bytes), terminated by a zero-length sub-block.</summary>
internal static class GifSubBlocks
{
    /// <summary>
    /// Reads every sub-block into one contiguous, <see cref="GifBufferPool"/>-rented buffer (the caller must
    /// <see cref="System.Buffers.ArrayPool{T}.Return(T[], bool)"/> it via <see cref="GifBufferPool.Shared"/>
    /// once done — the returned array's length is its pooled capacity, not the actual data length, hence the
    /// separate <c>Length</c>). Bounded by <see cref="GifDecodingLimits.MaxExtensionBytes"/>.
    /// </summary>
    public static (byte[] Buffer, int Length) ReadAll(Stream stream) => ReadAll(stream, GifDecodingLimits.MaxExtensionBytes);

    /// <summary>Same as <see cref="ReadAll(Stream)"/>, for a single frame's LZW image data. Bounded by <see cref="GifDecodingLimits.MaxImageDataBytes"/>.</summary>
    public static (byte[] Buffer, int Length) ReadAllImageData(Stream stream) => ReadAll(stream, GifDecodingLimits.MaxImageDataBytes);

    private static (byte[] Buffer, int Length) ReadAll(Stream stream, long maxBytes)
    {
        var pool = GifBufferPool.Shared;
        byte[] buffer = pool.Rent(4096);
        int length = 0;
        Span<byte> chunk = stackalloc byte[byte.MaxValue];

        try
        {
            while (GifStreamHelpers.TryReadByte(stream, out byte size) && size != 0)
            {
                if (length + size > maxBytes)
                {
                    throw new GifDecodingException("GIF sub-block data exceeds the maximum allowed size.");
                }

                if (length + size > buffer.Length)
                {
                    byte[] bigger = pool.Rent(Math.Max(buffer.Length * 2, length + size));
                    buffer.AsSpan(0, length).CopyTo(bigger);
                    pool.Return(buffer);
                    buffer = bigger;
                }

                var slice = chunk[..size];
                GifStreamHelpers.ReadExactlyOrThrow(stream, slice);
                slice.CopyTo(buffer.AsSpan(length, size));
                length += size;
            }
        }
        catch
        {
            pool.Return(buffer);
            throw;
        }

        return (buffer, length);
    }

    /// <summary>Discards every sub-block without retaining its contents.</summary>
    public static void SkipAll(Stream stream)
    {
        Span<byte> chunk = stackalloc byte[byte.MaxValue];
        while (GifStreamHelpers.TryReadByte(stream, out byte size) && size != 0)
        {
            GifStreamHelpers.ReadExactlyOrThrow(stream, chunk[..size]);
        }
    }
}
