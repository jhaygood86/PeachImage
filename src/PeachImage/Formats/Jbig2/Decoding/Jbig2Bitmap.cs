using System.Buffers;
using System.Numerics;
using PeachImage.Formats.Jbig2.Internal;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>A bilevel bitmap with one byte per pixel (0 white, 1 black), which keeps context formation and compositing simple and vectorizable.</summary>
internal sealed class Jbig2Bitmap : IDisposable
{
    private bool _disposed;

    public Jbig2Bitmap(int width, int height, byte fill = 0)
    {
        if (width < 0 || height < 0 || (long)width * height > Jbig2DecodingLimits.MaxPixelCount)
        {
            throw new Jbig2DecodingException($"JBIG2 bitmap size {width}x{height} is out of range.");
        }

        Width = width;
        Height = height;
        Data = ArrayPool<byte>.Shared.Rent(Math.Max(1, width * height));
        Data.AsSpan(0, width * height).Fill(fill);
        Jbig2BitmapScope.Register(this);
    }

    /// <summary>The number of pixels (the rented array can be longer).</summary>
    public int Length => Width * Height;

    /// <summary>Returns the pixel storage to the pool. Safe to call more than once; the bitmap must not be used afterwards.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            ArrayPool<byte>.Shared.Return(Data);
            Data = [];
        }
    }

    public int Width { get; }

    public int Height { get; private set; }

    public byte[] Data { get; private set; }

    public Span<byte> Row(int y) => Data.AsSpan(y * Width, Width);

    public int GetPixel(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? Data[(y * Width) + x] : 0;

    /// <summary>Grows (or shrinks) the bitmap to <paramref name="height"/> rows, filling new rows with <paramref name="fill"/>.</summary>
    public void SetHeight(int height, byte fill)
    {
        if (height == Height)
        {
            return;
        }

        if (height < 0 || (long)Width * height > Jbig2DecodingLimits.MaxPixelCount)
        {
            throw new Jbig2DecodingException($"JBIG2 page height {height} is out of range.");
        }

        var data = ArrayPool<byte>.Shared.Rent(Math.Max(1, Width * height));
        int keep = Math.Min(Height, height) * Width;
        Data.AsSpan(0, keep).CopyTo(data);
        if (height > Height)
        {
            data.AsSpan(keep, (Width * height) - keep).Fill(fill);
        }

        ArrayPool<byte>.Shared.Return(Data);
        Data = data;
        Height = height;
    }

    /// <summary>Copies a rectangle out of this bitmap; areas outside it are white.</summary>
    public Jbig2Bitmap Extract(int x, int y, int width, int height)
    {
        var result = new Jbig2Bitmap(width, height);
        for (int row = 0; row < height; row++)
        {
            int sy = y + row;
            if ((uint)sy >= (uint)Height)
            {
                continue;
            }

            int sx0 = Math.Max(x, 0);
            int sx1 = Math.Min(x + width, Width);
            if (sx1 > sx0)
            {
                Data.AsSpan((sy * Width) + sx0, sx1 - sx0).CopyTo(result.Row(row).Slice(sx0 - x));
            }
        }

        return result;
    }

    /// <summary>Draws <paramref name="source"/> at (<paramref name="x"/>, <paramref name="y"/>) with a JBIG2 combination operator: 0 OR, 1 AND, 2 XOR, 3 XNOR, 4 REPLACE.</summary>
    public void Combine(Jbig2Bitmap source, int x, int y, int op)
    {
        int x0 = Math.Max(x, 0);
        int x1 = (int)Math.Min((long)x + source.Width, Width);
        int y0 = Math.Max(y, 0);
        int y1 = (int)Math.Min((long)y + source.Height, Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        int count = x1 - x0;
        for (int row = y0; row < y1; row++)
        {
            var destination = Data.AsSpan((row * Width) + x0, count);
            var from = source.Data.AsSpan(((row - y) * source.Width) + (x0 - x), count);
            CombineRow(destination, from, op);
        }
    }

    private static void CombineRow(Span<byte> destination, ReadOnlySpan<byte> source, int op)
    {
        int i = 0;
        int lanes = Vector<byte>.Count;
        if (op == 4)
        {
            source.CopyTo(destination);
            return;
        }

        if (Vector.IsHardwareAccelerated && destination.Length >= lanes)
        {
            var one = Vector<byte>.One;
            for (; i <= destination.Length - lanes; i += lanes)
            {
                var d = new Vector<byte>(destination.Slice(i));
                var s = new Vector<byte>(source.Slice(i));
                var r = op switch
                {
                    0 => d | s,
                    1 => d & s,
                    2 => d ^ s,
                    _ => (d ^ s) ^ one,
                };
                r.CopyTo(destination.Slice(i));
            }
        }

        for (; i < destination.Length; i++)
        {
            destination[i] = op switch
            {
                0 => (byte)(destination[i] | source[i]),
                1 => (byte)(destination[i] & source[i]),
                2 => (byte)(destination[i] ^ source[i]),
                _ => (byte)((destination[i] ^ source[i]) ^ 1),
            };
        }
    }
}
