using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using PeachImage.Formats.Shared.Vectors;

namespace PeachImage.Formats.Shared.Orientation;

/// <summary>
/// Cache-blocked transposes behind the four orientations that swap width and height. A plain row-by-row transpose reads a row
/// sequentially but writes a whole column per row, touching a new cache line per pixel; walking <see cref="TileSize"/> x
/// <see cref="TileSize"/> pixel tiles keeps both the source tile and the destination tile resident while it is copied. All four
/// orientations are one transpose with the destination index flipped along either axis (a negative step), so there is a single
/// kernel per pixel size. 4-byte pixels additionally run a <see cref="Vector128{T}"/> 4x4 transpose inside each tile.
/// </summary>
internal static unsafe class PixelTransposer
{
    private const int TileSize = 32;

    // Larger pixels fill the same cache footprint with fewer of them.
    private const int WideTileSize = 16;

    /// <summary>
    /// Writes the transpose of the <paramref name="width"/> x <paramref name="height"/> image at <paramref name="src"/> to
    /// <paramref name="dst"/> (<paramref name="height"/> wide, <paramref name="width"/> tall). Source pixel (x, y) lands at
    /// (y, x), mirrored horizontally when <paramref name="flipX"/> (giving a 90 degree clockwise rotation) and vertically when
    /// <paramref name="flipY"/> (giving 90 counter-clockwise); both together give the transverse. The buffers must not overlap.
    /// </summary>
    public static void Transpose(byte* src, byte* dst, int width, int height, int bytesPerPixel, bool flipX, bool flipY)
    {
        // Destination element (in pixels) of source pixel (x, y) is origin + x * stepX + y * stepY.
        int stepX = flipY ? -height : height;
        int stepY = flipX ? -1 : 1;
        int origin = (flipY ? (width - 1) * height : 0) + (flipX ? height - 1 : 0);

        switch (bytesPerPixel)
        {
            case 1: Run((byte*)src, (byte*)dst, width, height, stepX, stepY, origin, TileSize); break;
            case 2: Run((ushort*)src, (ushort*)dst, width, height, stepX, stepY, origin, TileSize); break;
            case 3: Run((Pixel3*)src, (Pixel3*)dst, width, height, stepX, stepY, origin, TileSize); break;
            case 4: RunWords((uint*)src, (uint*)dst, width, height, stepX, stepY, origin); break;
            case 6: Run((Pixel6*)src, (Pixel6*)dst, width, height, stepX, stepY, origin, TileSize); break;
            case 8: Run((ulong*)src, (ulong*)dst, width, height, stepX, stepY, origin, TileSize); break;
            case 12: Run((Pixel12*)src, (Pixel12*)dst, width, height, stepX, stepY, origin, WideTileSize); break;
            case 16: Run((Vector128<byte>*)src, (Vector128<byte>*)dst, width, height, stepX, stepY, origin, WideTileSize); break;
            default: throw new NotSupportedException($"Unsupported pixel size {bytesPerPixel}.");
        }
    }

    /// <summary>Transposes the square <paramref name="size"/> x <paramref name="size"/> image at <paramref name="data"/> in place.</summary>
    public static void TransposeInPlace(byte* data, int size, int bytesPerPixel)
    {
        switch (bytesPerPixel)
        {
            case 1: TransposeInPlaceTiled((byte*)data, size, TileSize); break;
            case 2: TransposeInPlaceTiled((ushort*)data, size, TileSize); break;
            case 3: TransposeInPlaceTiled((Pixel3*)data, size, TileSize); break;
            case 4: TransposeInPlaceTiled((uint*)data, size, TileSize); break;
            case 6: TransposeInPlaceTiled((Pixel6*)data, size, TileSize); break;
            case 8: TransposeInPlaceTiled((ulong*)data, size, TileSize); break;
            case 12: TransposeInPlaceTiled((Pixel12*)data, size, WideTileSize); break;
            case 16: TransposeInPlaceTiled((Vector128<byte>*)data, size, WideTileSize); break;
            default: throw new NotSupportedException($"Unsupported pixel size {bytesPerPixel}.");
        }
    }

    private static void Run<T>(T* src, T* dst, int width, int height, int stepX, int stepY, int origin, int tile)
        where T : unmanaged
    {
        for (int ty = 0; ty < height; ty += tile)
        {
            int yEnd = Math.Min(ty + tile, height);
            for (int tx = 0; tx < width; tx += tile)
            {
                CopyRect(src, dst, width, tx, Math.Min(tx + tile, width), ty, yEnd, stepX, stepY, origin);
            }
        }
    }

    private static void RunWords(uint* src, uint* dst, int width, int height, int stepX, int stepY, int origin)
    {
        bool vectorize = VectorShuffle.IsSupported;
        for (int ty = 0; ty < height; ty += TileSize)
        {
            int yEnd = Math.Min(ty + TileSize, height);
            for (int tx = 0; tx < width; tx += TileSize)
            {
                int xEnd = Math.Min(tx + TileSize, width);
                if (!vectorize)
                {
                    CopyRect(src, dst, width, tx, xEnd, ty, yEnd, stepX, stepY, origin);
                    continue;
                }

                // The 4x4 blocks cover the tile's multiple-of-4 interior; the ragged right and bottom edges go through the scalar copy.
                int xVec = tx + ((xEnd - tx) & ~3);
                int yVec = ty + ((yEnd - ty) & ~3);
                for (int y = ty; y < yVec; y += 4)
                {
                    for (int x = tx; x < xVec; x += 4)
                    {
                        TransposeBlock4x4(src + ((nint)y * width) + x, width, dst + origin + (x * stepX), stepX, stepY, y);
                    }
                }

                CopyRect(src, dst, width, xVec, xEnd, ty, yEnd, stepX, stepY, origin);
                CopyRect(src, dst, width, tx, xVec, yVec, yEnd, stepX, stepY, origin);
            }
        }
    }

    // Copies source pixels [xStart, xEnd) x [yStart, yEnd) one element at a time to origin + x * stepX + y * stepY.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRect<T>(T* src, T* dst, int width, int xStart, int xEnd, int yStart, int yEnd, int stepX, int stepY, int origin)
        where T : unmanaged
    {
        for (int y = yStart; y < yEnd; y++)
        {
            T* s = src + ((nint)y * width) + xStart;
            T* d = dst + origin + (xStart * stepX) + (y * stepY);
            for (int x = xStart; x < xEnd; x++)
            {
                *d = *s;
                s++;
                d += stepX;
            }
        }
    }

    // Transposes the 4x4 block whose top-left source pixel is at `s` and writes source column i of the block as one 4-pixel run
    // at dstColumn0 + i * stepX, covering destination rows y .. y + 3 (reversed in memory when stepY is -1).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransposeBlock4x4(uint* s, int srcStride, uint* dstColumn0, int stepX, int stepY, int y)
    {
        Vector128<uint> r0;
        Vector128<uint> r1;
        Vector128<uint> r2;
        Vector128<uint> r3;
        uint* lowest;
        if (stepY > 0)
        {
            r0 = Vector128.Load(s);
            r1 = Vector128.Load(s + srcStride);
            r2 = Vector128.Load(s + (2 * srcStride));
            r3 = Vector128.Load(s + (3 * srcStride));
            lowest = dstColumn0 + y;
        }
        else
        {
            // Mirrored along the run: take the rows bottom-up and store at the run's lowest address.
            r3 = Vector128.Load(s);
            r2 = Vector128.Load(s + srcStride);
            r1 = Vector128.Load(s + (2 * srcStride));
            r0 = Vector128.Load(s + (3 * srcStride));
            lowest = dstColumn0 - (y + 3);
        }

        // Interleave row pairs as 32-bit elements, then the results as 64-bit elements.
        var lo01 = VectorShuffle.ZipLower(r0, r1).AsUInt64();
        var hi01 = VectorShuffle.ZipUpper(r0, r1).AsUInt64();
        var lo23 = VectorShuffle.ZipLower(r2, r3).AsUInt64();
        var hi23 = VectorShuffle.ZipUpper(r2, r3).AsUInt64();

        VectorShuffle.ZipLower(lo01, lo23).AsUInt32().Store(lowest);
        VectorShuffle.ZipUpper(lo01, lo23).AsUInt32().Store(lowest + stepX);
        VectorShuffle.ZipLower(hi01, hi23).AsUInt32().Store(lowest + (2 * stepX));
        VectorShuffle.ZipUpper(hi01, hi23).AsUInt32().Store(lowest + (3 * stepX));
    }

    private static void TransposeInPlaceTiled<T>(T* data, int size, int tile)
        where T : unmanaged
    {
        for (int ty = 0; ty < size; ty += tile)
        {
            int yEnd = Math.Min(ty + tile, size);
            for (int tx = ty; tx < size; tx += tile)
            {
                int xEnd = Math.Min(tx + tile, size);
                for (int y = ty; y < yEnd; y++)
                {
                    // On the diagonal tile only the part above the diagonal is swapped, so each pair is swapped exactly once.
                    for (int x = tx == ty ? y + 1 : tx; x < xEnd; x++)
                    {
                        T* a = data + ((nint)y * size) + x;
                        T* b = data + ((nint)x * size) + y;
                        (*a, *b) = (*b, *a);
                    }
                }
            }
        }
    }
}
