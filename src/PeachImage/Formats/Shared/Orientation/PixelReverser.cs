using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Shared.Orientation;

/// <summary>
/// Reverses the order of a run of whole pixels, the primitive behind horizontal mirroring (one row at a time) and a 180 degree
/// rotation (the whole buffer at once, since reversing every pixel of a tightly packed image is exactly that rotation).
/// Pixel sizes that divide 16 reverse a <see cref="Vector128{T}"/> at a time with a byte shuffle that reverses the pixels within
/// the vector; 3-byte pixels use the same shuffle on 4-pixel groups; everything else, and any machine without
/// <see cref="Vector128.IsHardwareAccelerated"/>, takes the typed scalar loops.
/// </summary>
internal static unsafe class PixelReverser
{
    private static readonly Vector128<byte>[] PowerOfTwoMasks = BuildPowerOfTwoMasks();

    // Within a 16-byte vector holding 16/bpp pixels: output pixel p takes input pixel (16/bpp - 1 - p), keeping its bytes in order.
    private static readonly Vector128<byte> Mask3 = Vector128.Create(
        (byte)9, 10, 11, 6, 7, 8, 3, 4, 5, 0, 1, 2, 12, 13, 14, 15);

    // Selects the 12 bytes of a 4-pixel group; the last 4 bytes of the vector are overhang that belongs to the neighbouring pixels.
    private static readonly Vector128<byte> Low12 = Vector128.Create(
        (byte)255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0);

    /// <summary>Copies <paramref name="pixelCount"/> pixels of <paramref name="bytesPerPixel"/> bytes from <paramref name="src"/> to <paramref name="dst"/> in reverse order. The buffers must not overlap.</summary>
    public static void Reverse(byte* src, byte* dst, int pixelCount, int bytesPerPixel)
    {
        switch (bytesPerPixel)
        {
            case 1:
            case 2:
            case 4:
            case 8:
            case 16:
                ReversePowerOfTwo(src, dst, pixelCount, bytesPerPixel);
                break;
            case 3:
                Reverse3(src, dst, pixelCount);
                break;
            default:
                ReverseScalar(src, dst, pixelCount, bytesPerPixel);
                break;
        }
    }

    /// <summary>Reverses <paramref name="pixelCount"/> pixels of <paramref name="bytesPerPixel"/> bytes in place.</summary>
    public static void ReverseInPlace(byte* data, int pixelCount, int bytesPerPixel)
    {
        switch (bytesPerPixel)
        {
            case 1:
            case 2:
            case 4:
            case 8:
            case 16:
                ReverseInPlacePowerOfTwo(data, pixelCount, bytesPerPixel);
                break;
            case 3:
                ReverseInPlace3(data, pixelCount);
                break;
            default:
                ReverseInPlaceScalar(data, pixelCount, bytesPerPixel);
                break;
        }
    }

    private static void ReversePowerOfTwo(byte* src, byte* dst, int pixelCount, int bytesPerPixel)
    {
        int totalBytes = pixelCount * bytesPerPixel;
        int vectors = totalBytes >> 4;
        if (!Vector128.IsHardwareAccelerated || vectors == 0)
        {
            ReverseScalar(src, dst, pixelCount, bytesPerPixel);
            return;
        }

        var mask = PowerOfTwoMasks[bytesPerPixel];
        for (int i = 0; i < vectors; i++)
        {
            Vector128.Shuffle(Vector128.Load(src + (i * 16)), mask).Store(dst + totalBytes - ((i + 1) * 16));
        }

        // The leftover pixels at the end of the source become the start of the destination.
        int done = vectors * 16;
        ReverseScalar(src + done, dst, (totalBytes - done) / bytesPerPixel, bytesPerPixel);
    }

    private static void ReverseInPlacePowerOfTwo(byte* data, int pixelCount, int bytesPerPixel)
    {
        int totalBytes = pixelCount * bytesPerPixel;
        int pairs = totalBytes >> 5;
        if (!Vector128.IsHardwareAccelerated || pairs == 0)
        {
            ReverseInPlaceScalar(data, pixelCount, bytesPerPixel);
            return;
        }

        var mask = PowerOfTwoMasks[bytesPerPixel];
        for (int i = 0; i < pairs; i++)
        {
            byte* left = data + (i * 16);
            byte* right = data + totalBytes - ((i + 1) * 16);
            var l = Vector128.Load(left);
            var r = Vector128.Load(right);
            Vector128.Shuffle(r, mask).Store(left);
            Vector128.Shuffle(l, mask).Store(right);
        }

        int done = pairs * 16;
        ReverseInPlaceScalar(data + done, (totalBytes - (2 * done)) / bytesPerPixel, bytesPerPixel);
    }

    // 4 pixels (12 bytes) per step through a 16-byte shuffle. The vector's last 4 bytes are junk (or neighbours), so each step
    // handles them differently from the power-of-two path: see the comments in the two methods.
    private static void Reverse3(byte* src, byte* dst, int pixelCount)
    {
        // Pixels 0 and 1 of dst are done by hand so the vector loop can start at d = 2: that keeps its 16-byte source loads
        // (which overhang the 12 useful bytes by 4) inside the buffer.
        if (!Vector128.IsHardwareAccelerated || pixelCount < 8)
        {
            ReverseScalar(src, dst, pixelCount, 3);
            return;
        }

        var s = (Pixel3*)src;
        var d = (Pixel3*)dst;
        d[0] = s[pixelCount - 1];
        d[1] = s[pixelCount - 2];

        int i = 2;

        // Each store spills 4 junk bytes into the next dst pixels, which the following store (or the scalar tail) overwrites.
        for (; i + 6 <= pixelCount; i += 4)
        {
            Vector128.Shuffle(Vector128.Load(src + (3 * (pixelCount - i - 4))), Mask3).Store(dst + (3 * i));
        }

        for (; i < pixelCount; i++)
        {
            d[i] = s[pixelCount - 1 - i];
        }
    }

    private static void ReverseInPlace3(byte* data, int pixelCount)
    {
        var p = (Pixel3*)data;
        int lo = 0;
        int hi = pixelCount - 1;

        // The first two pairs are swapped by hand so the vector loop's 16-byte loads (4 bytes more than the 4 pixels they use)
        // stay inside the buffer at both ends.
        while (lo < hi && lo < 2)
        {
            (p[lo], p[hi]) = (p[hi], p[lo]);
            lo++;
            hi--;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            while (lo + 4 <= hi - 3)
            {
                byte* left = data + (3 * lo);
                byte* right = data + (3 * (hi - 3));
                var l = Vector128.Load(left);
                var r = Vector128.Load(right);

                // Each 16-byte store also covers the 4 bytes after its 12; refilling them with the bytes that were loaded from the
                // destination (not from the source block) makes the overhang a no-op, so no neighbouring pixel is disturbed.
                Vector128.ConditionalSelect(Low12, Vector128.Shuffle(r, Mask3), l).Store(left);
                Vector128.ConditionalSelect(Low12, Vector128.Shuffle(l, Mask3), r).Store(right);
                lo += 4;
                hi -= 4;
            }
        }

        for (; lo < hi; lo++, hi--)
        {
            (p[lo], p[hi]) = (p[hi], p[lo]);
        }
    }

    private static void ReverseScalar(byte* src, byte* dst, int pixelCount, int bytesPerPixel)
    {
        switch (bytesPerPixel)
        {
            case 1: ReverseScalar((byte*)src, (byte*)dst, pixelCount); break;
            case 2: ReverseScalar((ushort*)src, (ushort*)dst, pixelCount); break;
            case 3: ReverseScalar((Pixel3*)src, (Pixel3*)dst, pixelCount); break;
            case 4: ReverseScalar((uint*)src, (uint*)dst, pixelCount); break;
            case 6: ReverseScalar((Pixel6*)src, (Pixel6*)dst, pixelCount); break;
            case 8: ReverseScalar((ulong*)src, (ulong*)dst, pixelCount); break;
            case 12: ReverseScalar((Pixel12*)src, (Pixel12*)dst, pixelCount); break;
            case 16: ReverseScalar((Vector128<byte>*)src, (Vector128<byte>*)dst, pixelCount); break;
            default: throw new NotSupportedException($"Unsupported pixel size {bytesPerPixel}.");
        }
    }

    private static void ReverseInPlaceScalar(byte* data, int pixelCount, int bytesPerPixel)
    {
        switch (bytesPerPixel)
        {
            case 1: ReverseInPlaceScalar((byte*)data, pixelCount); break;
            case 2: ReverseInPlaceScalar((ushort*)data, pixelCount); break;
            case 3: ReverseInPlaceScalar((Pixel3*)data, pixelCount); break;
            case 4: ReverseInPlaceScalar((uint*)data, pixelCount); break;
            case 6: ReverseInPlaceScalar((Pixel6*)data, pixelCount); break;
            case 8: ReverseInPlaceScalar((ulong*)data, pixelCount); break;
            case 12: ReverseInPlaceScalar((Pixel12*)data, pixelCount); break;
            case 16: ReverseInPlaceScalar((Vector128<byte>*)data, pixelCount); break;
            default: throw new NotSupportedException($"Unsupported pixel size {bytesPerPixel}.");
        }
    }

    private static void ReverseScalar<T>(T* src, T* dst, int pixelCount)
        where T : unmanaged
    {
        T* end = dst + pixelCount;
        for (int i = 0; i < pixelCount; i++)
        {
            *--end = src[i];
        }
    }

    private static void ReverseInPlaceScalar<T>(T* data, int pixelCount)
        where T : unmanaged
    {
        for (int lo = 0, hi = pixelCount - 1; lo < hi; lo++, hi--)
        {
            (data[lo], data[hi]) = (data[hi], data[lo]);
        }
    }

    private static Vector128<byte>[] BuildPowerOfTwoMasks()
    {
        var masks = new Vector128<byte>[17];
        Span<byte> indices = stackalloc byte[16];
        for (int bytesPerPixel = 1; bytesPerPixel <= 16; bytesPerPixel <<= 1)
        {
            for (int i = 0; i < 16; i++)
            {
                indices[i] = (byte)(16 - (bytesPerPixel * ((i / bytesPerPixel) + 1)) + (i % bytesPerPixel));
            }

            masks[bytesPerPixel] = Vector128.Create<byte>(indices);
        }

        return masks;
    }
}
