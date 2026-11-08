using System.Runtime.Intrinsics;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Runs VP8L's inverse transforms over a few image rows at a time, in reverse declaration order, the way
/// libwebp's <c>ProcessRows</c> does. The decoded pixel buffer is only ever read (backward references still need
/// the untransformed residuals), and the transformed rows live in two small scratch buffers of a batch's worth
/// of rows, so every transform and the final conversion touch data that is still in cache instead of making a
/// full pass over an image-sized buffer.
/// </summary>
/// <remarks>
/// <para>
/// Each stage is either in place on the current scratch buffer (subtract-green, cross-color, predictor) or maps
/// between two buffers (color indexing, which also changes the row width). A stage that would have to modify the
/// decoded buffer first copies its rows into scratch. The predictor needs the transformed row above its first
/// row; that row is saved after each batch and put back in the slack row that precedes the scratch rows.
/// </para>
/// <para>
/// The format allows each transform kind at most once, which is what makes a single saved "top row" enough.
/// </para>
/// </remarks>
internal sealed class Vp8LRowTransformPipeline : IDisposable
{
    /// <summary>Rows transformed per call; small enough that a batch of even a 16383-wide image stays within a few MB.</summary>
    public const int BatchRows = 16;

    private readonly List<Vp8LTransform> _transforms;
    private readonly int _imageWidth;
    private readonly uint[] _bufferA;
    private readonly uint[] _bufferB;
    private readonly uint[]? _predictorTopRow;

    /// <param name="transforms">The transforms in the order the bitstream declared them.</param>
    /// <param name="imageWidth">The final (un-bundled) image width, the widest any stage gets.</param>
    public Vp8LRowTransformPipeline(List<Vp8LTransform> transforms, int imageWidth)
    {
        _transforms = transforms;
        _imageWidth = imageWidth;

        // One slack row ahead of the rows, so the predictor's "row above" sits directly before them.
        int length = checked((BatchRows + 1) * imageWidth);
        _bufferA = WebpBufferPool.SharedUInt32.Rent(length);
        _bufferB = WebpBufferPool.SharedUInt32.Rent(length);

        foreach (var transform in transforms)
        {
            if (transform.Type == Vp8LTransformType.Predictor)
            {
                _predictorTopRow = new uint[imageWidth];
            }
        }
    }

    /// <summary>
    /// Transforms rows <paramref name="yStart"/> to <paramref name="yEnd"/> (at most <see cref="BatchRows"/>) of
    /// <paramref name="decoded"/>, whose rows are <paramref name="decodedWidth"/> wide, and returns the final
    /// image rows. Batches must be processed in order, each starting where the last ended.
    /// </summary>
    public ReadOnlySpan<uint> Process(uint[] decoded, int decodedWidth, int yStart, int yEnd)
    {
        int rowCount = yEnd - yStart;
        ReadOnlySpan<uint> current = decoded.AsSpan(yStart * decodedWidth, rowCount * decodedWidth);
        uint[]? currentBuffer = null;
        int rowsOffset = _imageWidth;

        for (int i = _transforms.Count - 1; i >= 0; i--)
        {
            var transform = _transforms[i];
            int width = transform.Xsize;
            int length = rowCount * width;

            if (transform.Type == Vp8LTransformType.ColorIndexing)
            {
                var destination = ReferenceEquals(currentBuffer, _bufferA) ? _bufferB : _bufferA;
                Vp8LColorIndexingTransform.ApplyInverseRows(current, destination.AsSpan(rowsOffset, length), transform, yStart, yEnd);
                currentBuffer = destination;
                current = destination.AsSpan(rowsOffset, length);
                continue;
            }

            // The predictor reads its residuals from the decoded buffer row by row as it reconstructs them; the
            // other two in-place transforms get a plain copy. Either way the decoded buffer stays untouched.
            ReadOnlySpan<uint> predictorSource = default;
            if (currentBuffer is null)
            {
                currentBuffer = _bufferA;
                if (transform.Type == Vp8LTransformType.Predictor)
                {
                    predictorSource = current;
                }
                else
                {
                    current.CopyTo(currentBuffer.AsSpan(rowsOffset, length));
                }
            }

            var rows = currentBuffer.AsSpan(rowsOffset, length);
            switch (transform.Type)
            {
                case Vp8LTransformType.SubtractGreen:
                    Vp8LSubtractGreenTransform.ApplyInverse(rows);
                    break;

                case Vp8LTransformType.CrossColor:
                    Vp8LColorTransform.ApplyInverseRows(rows, transform, yStart, yEnd);
                    break;

                default:
                    var top = _predictorTopRow!;
                    if (yStart > 0)
                    {
                        top.AsSpan(0, width).CopyTo(currentBuffer.AsSpan(rowsOffset - width, width));
                    }

                    Vp8LPredictorTransform.ApplyInverseRows(currentBuffer, rowsOffset, transform, yStart, yEnd, predictorSource);
                    currentBuffer.AsSpan(rowsOffset + ((rowCount - 1) * width), width).CopyTo(top);
                    break;
            }

            current = rows;
        }

        return current;
    }

    public void Dispose()
    {
        WebpBufferPool.SharedUInt32.Return(_bufferA);
        WebpBufferPool.SharedUInt32.Return(_bufferB);
    }
}

/// <summary>Converts decoded ARGB pixels to the byte layouts <see cref="Image"/> stores.</summary>
internal static class Vp8LPixelPacker
{
    // A little-endian ARGB word is the bytes B, G, R, A in memory.
    private static readonly Vector128<byte> RgbaOrder = Vector128.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    // Four pixels to twelve bytes of R, G, B; the last four lanes are zeroed and overwritten by the next group.
    private static readonly Vector128<byte> RgbOrder = Vector128.Create(
        (byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>Writes <paramref name="argb"/> as RGB24 (alpha dropped) or RGBA32 into <paramref name="destination"/>, which must have room for every pixel.</summary>
    public static void Pack(ReadOnlySpan<uint> argb, Span<byte> destination, bool withAlpha)
    {
        int bytesPerPixel = withAlpha ? 4 : 3;
        if (destination.Length < argb.Length * bytesPerPixel)
        {
            throw new ArgumentException("Destination is too small for the pixel run.", nameof(destination));
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ReadOnlySpan<byte> source = System.Runtime.InteropServices.MemoryMarshal.AsBytes(argb);

            if (withAlpha)
            {
                for (; x + 4 <= argb.Length; x += 4)
                {
                    Vector128.Shuffle(Vector128.Create(source.Slice(x * 4, 16)), RgbaOrder).CopyTo(destination.Slice(x * 4, 16));
                }
            }
            else
            {
                // Each group stores 16 bytes for 12 of output, so it needs 16 bytes of destination left.
                for (; x + 4 <= argb.Length && (x * 3) + 16 <= destination.Length; x += 4)
                {
                    Vector128.Shuffle(Vector128.Create(source.Slice(x * 4, 16)), RgbOrder).CopyTo(destination.Slice(x * 3, 16));
                }
            }
        }

        for (; x < argb.Length; x++)
        {
            uint pixel = argb[x];
            int o = x * bytesPerPixel;
            destination[o] = (byte)(pixel >> 16);
            destination[o + 1] = (byte)(pixel >> 8);
            destination[o + 2] = (byte)pixel;
            if (withAlpha)
            {
                destination[o + 3] = (byte)(pixel >> 24);
            }
        }
    }
}
