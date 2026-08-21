using System.Runtime.InteropServices;
using PeachImage.Formats.Webp.Internal;
using PeachImage.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Top-level orchestration for the VP8L lossless bitstream decoder: bit reader, canonical-Huffman table
/// build, LZ77 backward references + color cache, and the four optional transforms (predictor, color,
/// subtract-green, color-indexing), applied in reverse of the order they were declared in the bitstream.
/// </summary>
/// <remarks>
/// Both <see cref="Decode"/> and <see cref="DecodeAlphaSubstream"/>'s pixel working buffers are rented from
/// <see cref="WebpBufferPool.SharedUInt32"/> rather than freshly allocated — for a 1080p image this buffer
/// alone is ~8 MiB, comfortably past the large-object-heap threshold, and a fresh LOH allocation on every
/// decode call was measured to be a real, significant source of Gen2 GC pressure (LOH is collected only as
/// part of a Gen2 collection). Every downstream consumer of this buffer (the four transform inverses,
/// <see cref="PackPixels"/>, <c>WebpAlphaDecoder</c>'s green-channel extraction loop) already works through
/// explicitly-bounded <c>Span&lt;uint&gt;</c>/length-bounded loops rather than trusting the array's own
/// <c>.Length</c>, which is what makes it safe for the rented array to be larger than the actual pixel count
/// (an <see cref="System.Buffers.ArrayPool{T}"/> contract) — callers are responsible for returning it once done.
/// </remarks>
internal static class Vp8LDecoder
{
    private const byte Signature = 0x2F;

    /// <summary>Decodes a full top-level VP8L image chunk (5-byte header + transforms + pixel stream) into an <see cref="Image"/>.</summary>
    public static Image Decode(ReadOnlyMemory<byte> vp8LBytes)
    {
        var (data, offset, end) = GetBounds(vp8LBytes);

        if (end - offset < 5 || data[offset] != Signature)
        {
            throw new WebpDecodingException("Malformed VP8L chunk: missing or invalid signature byte.");
        }

        var reader = new Vp8LBitReader(data, offset + 1, end);
        int width = (int)reader.ReadBits(14) + 1;
        int height = (int)reader.ReadBits(14) + 1;
        bool alphaIsUsed = reader.ReadBits(1) != 0;
        reader.ReadBits(3); // version_number -- always 0, nothing to branch on.

        ValidateDimensions(width, height);

        var (argb, pixelCount, isPooled) = DecodeCore(reader, width, height, rentFromPool: true);
        try
        {
            var pixelFormat = alphaIsUsed ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
            byte[] packed = PackPixels(argb.AsSpan(0, pixelCount), pixelFormat);
            return Image.FromBuffer(width, height, pixelFormat, packed, owned: true);
        }
        finally
        {
            if (isPooled)
            {
                WebpBufferPool.SharedUInt32.Return(argb);
            }
        }
    }

    /// <summary>
    /// Decodes a VP8L pixel stream with no 5-byte header (used by the ALPH chunk's "lossless" alpha
    /// compression method) into a flat ARGB buffer of the given, already-known dimensions. The returned array
    /// may be larger than <c>width*height</c> when <paramref name="isPooled"/> is <see langword="true"/> (an
    /// <see cref="System.Buffers.ArrayPool{T}"/> contract) — the caller must bound every read to exactly
    /// <c>width*height</c> elements and return it via <see cref="WebpBufferPool.SharedUInt32"/> once done.
    /// </summary>
    public static uint[] DecodeAlphaSubstream(ReadOnlyMemory<byte> bytes, int width, int height, out bool isPooled)
    {
        ValidateDimensions(width, height);

        var (data, offset, end) = GetBounds(bytes);
        var reader = new Vp8LBitReader(data, offset, end);
        var (pixels, _, pooled) = DecodeCore(reader, width, height, rentFromPool: true);
        isPooled = pooled;
        return pixels;
    }

    /// <summary>
    /// The core shared by both public entry points: reads the transform declarations, decodes the pixel
    /// stream at whatever (possibly color-indexing-reduced) width that implies, then unwinds every
    /// transform's inverse in reverse declaration order. Called with a full 5-byte-header-and-VP8X-agnostic
    /// bit reader already positioned at the transform-presence bit, by both <see cref="Decode"/> and
    /// <see cref="DecodeAlphaSubstream"/> alike — the ALPH lossless substream has full parity with a
    /// top-level VP8L image stream (transforms, color cache, and meta-Huffman are all still allowed), it
    /// just skips the outer 5-byte width/height/alpha header. Returns whether the result is pooled. A
    /// <see cref="Vp8LTransformType.ColorIndexing"/> transform always replaces the buffer with a new,
    /// full-width one (the original rented buffer is returned to the pool immediately at that point, not left
    /// for the caller to deal with) — but the replacement is rented from the same pool under the same
    /// <paramref name="rentFromPool"/> flag the original was, so a color-indexed image doesn't fall back to an
    /// unpooled allocation just because its buffer changed size partway through.
    /// </summary>
    private static (uint[] Pixels, int PixelCount, bool IsPooled) DecodeCore(Vp8LBitReader reader, int width, int height, bool rentFromPool)
    {
        int transformXsize = width;
        var transforms = Vp8LTransformReader.ReadTransforms(reader, ref transformXsize, height);

        uint[] pixels = Vp8LPixelDecoder.DecodeImageStream(reader, transformXsize, height, allowRecursion: true, rentFromPool);
        bool isPooled = rentFromPool;
        int pixelCount = transformXsize * height;

        for (int i = transforms.Count - 1; i >= 0; i--)
        {
            var transform = transforms[i];
            switch (transform.Type)
            {
                case Vp8LTransformType.SubtractGreen:
                    Vp8LSubtractGreenTransform.ApplyInverse(pixels.AsSpan(0, pixelCount));
                    break;

                case Vp8LTransformType.Predictor:
                    Vp8LPredictorTransform.ApplyInverse(pixels.AsSpan(0, pixelCount), transform);
                    break;

                case Vp8LTransformType.CrossColor:
                    Vp8LColorTransform.ApplyInverse(pixels.AsSpan(0, pixelCount), transform);
                    break;

                case Vp8LTransformType.ColorIndexing:
                    // The expansion always replaces the buffer with a new, full-width one -- rented from the
                    // same pool under the same rentFromPool flag the original buffer was, so this doesn't
                    // regress to an unpooled allocation on the one path that used to be exactly that.
                    uint[] expanded = Vp8LColorIndexingTransform.ApplyInverse(pixels.AsSpan(0, pixelCount), transform, rentFromPool);
                    if (isPooled)
                    {
                        WebpBufferPool.SharedUInt32.Return(pixels);
                    }

                    pixels = expanded;
                    isPooled = rentFromPool;
                    pixelCount = transform.Xsize * transform.Ysize;
                    break;
            }
        }

        return (pixels, pixelCount, isPooled);
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > WebpDecodingLimits.MaxDimension || height > WebpDecodingLimits.MaxDimension)
        {
            throw new WebpDecodingException($"Invalid VP8L image dimensions {width}x{height}.");
        }

        if ((long)width * height > WebpDecodingLimits.MaxPixelCount)
        {
            throw new WebpDecodingException($"VP8L image {width}x{height} exceeds the maximum supported pixel count.");
        }
    }

    private static byte[] PackPixels(ReadOnlySpan<uint> argb, PixelFormat format)
    {
        int bytesPerPixel = format == PixelFormat.Rgba32 ? 4 : 3;
        int byteCount = checked((int)((long)argb.Length * bytesPerPixel));
        var buffer = ImageBufferPool.Shared.Rent(byteCount);

        int o = 0;
        for (int i = 0; i < argb.Length; i++)
        {
            uint pixel = argb[i];
            buffer[o++] = (byte)(pixel >> 16); // red
            buffer[o++] = (byte)(pixel >> 8);  // green
            buffer[o++] = (byte)pixel;         // blue

            if (bytesPerPixel == 4)
            {
                buffer[o++] = (byte)(pixel >> 24); // alpha
            }
        }

        return buffer;
    }

    /// <summary>Recovers the backing array (and valid byte range within it) from a <see cref="ReadOnlyMemory{T}"/> without copying when possible, falling back to a copy only when the memory isn't array-backed.</summary>
    private static (byte[] Data, int Offset, int End) GetBounds(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return (segment.Array, segment.Offset, segment.Offset + segment.Count);
        }

        byte[] copy = memory.ToArray();
        return (copy, 0, copy.Length);
    }
}
