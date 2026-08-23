using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Shared.Compositing;

/// <summary>
/// Encodes a single RGBA color into a <see cref="PixelFormat"/>'s native byte layout and stamps it across an
/// entire pixel buffer — used to initialize the background of a <see cref="ResizeMode.Pad"/> canvas (which
/// <see cref="Image.Create"/> returns uninitialized).
/// </summary>
internal static class PixelFormatFill
{
    /// <summary>Fills every pixel of <paramref name="image"/> with the given RGBA color, converted to its <see cref="PixelFormat"/>.</summary>
    public static void Fill(Image image, byte r, byte g, byte b, byte a)
    {
        int bytesPerPixel = image.PixelFormat.GetBytesPerPixel();
        Span<byte> pixel = stackalloc byte[8]; // Rgba64 (8 bytes) is the widest pixel format.
        var encoded = pixel[..bytesPerPixel];
        EncodeColor(image.PixelFormat, r, g, b, a, encoded);

        FillPattern(image.GetPixelSpan(), encoded);
    }

    /// <summary>Encodes an RGBA color into <paramref name="destination"/>'s bytes for the given <paramref name="format"/>.</summary>
    internal static void EncodeColor(PixelFormat format, byte r, byte g, byte b, byte a, Span<byte> destination)
    {
        switch (format)
        {
            case PixelFormat.Gray8:
                destination[0] = Luma8(r, g, b);
                break;

            case PixelFormat.Rgb24:
                destination[0] = r;
                destination[1] = g;
                destination[2] = b;
                break;

            case PixelFormat.Rgba32:
                destination[0] = r;
                destination[1] = g;
                destination[2] = b;
                destination[3] = a;
                break;

            case PixelFormat.Cmyk32:
                // Naive best-effort inverse of the naive CMYK->RGB decode formula used elsewhere in this
                // codebase (R = 255 - min(255, C+K)); no encoder actually supports Cmyk32 output today, so
                // this only affects raw in-memory bytes on a Pad result.
                destination[0] = (byte)(255 - r);
                destination[1] = (byte)(255 - g);
                destination[2] = (byte)(255 - b);
                destination[3] = 0;
                break;

            case PixelFormat.Gray16:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(destination);
                samples[0] = Widen(Luma8(r, g, b));
                break;
            }

            case PixelFormat.Rgb48:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(destination);
                samples[0] = Widen(r);
                samples[1] = Widen(g);
                samples[2] = Widen(b);
                break;
            }

            case PixelFormat.Rgba64:
            {
                var samples = MemoryMarshal.Cast<byte, ushort>(destination);
                samples[0] = Widen(r);
                samples[1] = Widen(g);
                samples[2] = Widen(b);
                samples[3] = Widen(a);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, message: null);
        }
    }

    // Matches PixelFormatConversionKernels' ITU-R BT.601 luma formula.
    private static byte Luma8(byte r, byte g, byte b) =>
        (byte)Math.Clamp(Math.Round((0.299 * r) + (0.587 * g) + (0.114 * b), MidpointRounding.AwayFromZero), 0, 255);

    // Matches PixelFormatConversionKernels.WidenBytesToUInt16's v -> (v << 8) | v convention.
    private static ushort Widen(byte v) => (ushort)((v << 8) | v);

    /// <summary>
    /// Stamps <paramref name="pattern"/> (one pixel's worth of bytes) repeatedly across <paramref name="destination"/>.
    /// Vectorized: builds a tile that repeats <paramref name="pattern"/> for a whole number of pixels that's
    /// also a whole number of vector widths (<c>Lcm(pattern.Length, vectorWidth) / vectorWidth</c> vectors),
    /// so every store lands on a pixel boundary regardless of whether <paramref name="pattern"/>'s length
    /// (1/2/3/4/6/8 bytes, depending on <see cref="PixelFormat"/>) evenly divides the vector width — the same
    /// problem <c>PixelFormatConversionKernels</c>' lo/hi-split stores solve for Rgb24's 3-byte stride. Tries
    /// Vector256 (AVX2) first, then Vector128, then a scalar byte-cycling loop, mirroring
    /// <c>ResamplingConvolverSelector</c>'s existing Vector256 &gt; Vector128 dispatch pattern.
    /// </summary>
    private static void FillPattern(Span<byte> destination, ReadOnlySpan<byte> pattern)
    {
        int stored = 0;
        if (Vector256.IsHardwareAccelerated && destination.Length >= Vector256<byte>.Count)
        {
            stored = FillTiled256(destination, pattern);
        }
        else if (Vector128.IsHardwareAccelerated && destination.Length >= Vector128<byte>.Count)
        {
            stored = FillTiled128(destination, pattern);
        }

        if (stored < destination.Length)
        {
            FillPatternScalar(destination[stored..], pattern, stored % pattern.Length);
        }
    }

    private static int FillTiled256(Span<byte> destination, ReadOnlySpan<byte> pattern)
    {
        const int vectorWidth = 32;
        var tile = BuildTile(pattern, vectorWidth);
        var tileVectors = new Vector256<byte>[tile.Length / vectorWidth];
        for (int i = 0; i < tileVectors.Length; i++)
        {
            tileVectors[i] = Vector256.Create<byte>(tile.AsSpan(i * vectorWidth, vectorWidth));
        }

        int offset = 0;
        int vectorIndex = 0;
        for (; offset + vectorWidth <= destination.Length; offset += vectorWidth)
        {
            tileVectors[vectorIndex].CopyTo(destination.Slice(offset, vectorWidth));
            vectorIndex = (vectorIndex + 1) % tileVectors.Length;
        }

        return offset;
    }

    private static int FillTiled128(Span<byte> destination, ReadOnlySpan<byte> pattern)
    {
        const int vectorWidth = 16;
        var tile = BuildTile(pattern, vectorWidth);
        var tileVectors = new Vector128<byte>[tile.Length / vectorWidth];
        for (int i = 0; i < tileVectors.Length; i++)
        {
            tileVectors[i] = Vector128.Create<byte>(tile.AsSpan(i * vectorWidth, vectorWidth));
        }

        int offset = 0;
        int vectorIndex = 0;
        for (; offset + vectorWidth <= destination.Length; offset += vectorWidth)
        {
            tileVectors[vectorIndex].CopyTo(destination.Slice(offset, vectorWidth));
            vectorIndex = (vectorIndex + 1) % tileVectors.Length;
        }

        return offset;
    }

    // Builds the smallest byte-cycling repetition of `pattern` whose length is a whole multiple of both
    // pattern.Length and vectorWidth (their LCM) — see the FillPattern remarks for why that's needed.
    private static byte[] BuildTile(ReadOnlySpan<byte> pattern, int vectorWidth)
    {
        int lcm = pattern.Length * vectorWidth / Gcd(pattern.Length, vectorWidth);
        var tile = new byte[lcm];
        for (int i = 0; i < lcm; i++)
        {
            tile[i] = pattern[i % pattern.Length];
        }

        return tile;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static void FillPatternScalar(Span<byte> destination, ReadOnlySpan<byte> pattern, int startPhase)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = pattern[(startPhase + i) % pattern.Length];
        }
    }
}
