using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reads/skips a run of size-prefixed GIF data sub-blocks (each up to 255 bytes), terminated by a zero-length sub-block.</summary>
internal static class GifSubBlocks
{
    /// <summary>Reads every sub-block into one contiguous buffer. Bounded by <see cref="GifDecodingLimits.MaxExtensionBytes"/>.</summary>
    public static byte[] ReadAll(Stream stream) => ReadAll(stream, GifDecodingLimits.MaxExtensionBytes);

    /// <summary>Reads every sub-block into one contiguous buffer, for a single frame's LZW image data. Bounded by <see cref="GifDecodingLimits.MaxImageDataBytes"/>.</summary>
    public static byte[] ReadAllImageData(Stream stream) => ReadAll(stream, GifDecodingLimits.MaxImageDataBytes);

    private static byte[] ReadAll(Stream stream, long maxBytes)
    {
        using var buffer = new MemoryStream();
        Span<byte> chunk = stackalloc byte[byte.MaxValue];
        while (GifStreamHelpers.TryReadByte(stream, out byte size) && size != 0)
        {
            if (buffer.Length + size > maxBytes)
            {
                throw new GifDecodingException("GIF sub-block data exceeds the maximum allowed size.");
            }

            var slice = chunk[..size];
            GifStreamHelpers.ReadExactlyOrThrow(stream, slice);
            buffer.Write(slice);
        }

        return buffer.ToArray();
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
