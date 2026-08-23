using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Interprets one row's worth of unpacked, unscaled samples (raw magnitudes for grayscale/RGB/CMYK, raw
/// palette indices for indexed color) into the destination row of the <see cref="TiffImageDescriptor.PixelFormat"/>
/// this file resolved to. A closed dispatch over that already-decided pixel format — every combination this
/// decoder doesn't handle was already rejected by <see cref="TiffValidation"/> before decode ever reaches
/// here, so the <see langword="default"/> arm below is an "unreachable, validation should have caught this"
/// assertion, not a user-facing unsupported-feature path.
/// </summary>
/// <remarks>
/// The per-pixel premultiplied-alpha un-scaling paths (<see cref="WriteRgba32Premultiplied"/>/
/// <see cref="WriteRgba64Premultiplied"/>) stay scalar, deliberately: they're an exact-integer-division operation,
/// and this decoder's own correctness tests compare bit-for-bit against an <c>ffmpeg</c>-generated reference
/// (see the project plan) — a vectorized reciprocal-approximation divide could round differently than the
/// scalar reference and fail that comparison for no real throughput win on what's already an uncommon
/// real-world case (a genuinely premultiplied-alpha source TIFF). Every other path below (which is either a
/// pure reshape/narrow or an exact power-of-two-friendly integer scale) is safe to vectorize because it has
/// only one possible correct per-lane result to begin with.
/// </remarks>
internal static class TiffSampleWriter
{
    public static void WriteRow(ReadOnlySpan<ushort> samples, TiffImageDescriptor descriptor, byte[] palette, Span<byte> destRow)
    {
        switch (descriptor.PixelFormat)
        {
            case PixelFormat.Gray8:
                WriteGray8(samples, destRow, invert: descriptor.Photometric == 0, descriptor.BitsPerSample);
                return;

            case PixelFormat.Gray16:
                WriteGray16(samples, destRow, invert: descriptor.Photometric == 0);
                return;

            case PixelFormat.Rgb24 when descriptor.Photometric == 3:
                WritePaletteIndices(samples, palette, destRow);
                return;

            case PixelFormat.Rgb24:
            case PixelFormat.Cmyk32:
                NarrowSamplesToBytes(samples, destRow);
                return;

            case PixelFormat.Rgba32 when !descriptor.AlphaIsPremultiplied:
                // Straight (non-premultiplied) alpha is stored contiguously as R,G,B,A per pixel, same layout
                // as the destination — identical operation to the non-palette Rgb24/Cmyk32 case above.
                NarrowSamplesToBytes(samples, destRow);
                return;

            case PixelFormat.Rgba32:
                WriteRgba32Premultiplied(samples, destRow);
                return;

            case PixelFormat.Rgb48:
                samples.CopyTo(MemoryMarshal.Cast<byte, ushort>(destRow));
                return;

            case PixelFormat.Rgba64 when !descriptor.AlphaIsPremultiplied:
                samples.CopyTo(MemoryMarshal.Cast<byte, ushort>(destRow));
                return;

            case PixelFormat.Rgba64:
                WriteRgba64Premultiplied(samples, destRow);
                return;

            default:
                throw new TiffDecodingException($"Unreachable: no sample writer for pixel format {descriptor.PixelFormat}.");
        }
    }

    private static void WriteGray8(ReadOnlySpan<ushort> samples, Span<byte> destRow, bool invert, int bitDepth)
    {
        ushort multiplier = bitDepth switch
        {
            1 => 255,
            2 => 85,
            4 => 17,
            8 => 1,
            _ => throw new TiffDecodingException($"Unreachable: unsupported grayscale bit depth {bitDepth}."),
        };

        int i = 0;
        if (multiplier > 1 || invert)
        {
            // Every value here is guaranteed <= 255 after the multiply (the multipliers above are chosen
            // exactly so raw*multiplier never exceeds 255 for that bit depth), so a plain narrow (no
            // saturating clamp) is correct.
            var multiplierVector256 = Vector256.Create(multiplier);
            var multiplierVector128 = Vector128.Create(multiplier);

            if (Vector256.IsHardwareAccelerated)
            {
                for (; i + 32 <= samples.Length; i += 32)
                {
                    var lo = Vector256.LoadUnsafe(in samples[i]) * multiplierVector256;
                    var hi = Vector256.LoadUnsafe(in samples[i + 16]) * multiplierVector256;
                    var narrowed = Vector256.Narrow(lo, hi);
                    (invert ? ~narrowed : narrowed).StoreUnsafe(ref destRow[i]);
                }
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                for (; i + 16 <= samples.Length; i += 16)
                {
                    var lo = Vector128.LoadUnsafe(in samples[i]) * multiplierVector128;
                    var hi = Vector128.LoadUnsafe(in samples[i + 8]) * multiplierVector128;
                    var narrowed = Vector128.Narrow(lo, hi);
                    (invert ? ~narrowed : narrowed).StoreUnsafe(ref destRow[i]);
                }
            }
        }

        for (; i < samples.Length; i++)
        {
            byte v = (byte)(samples[i] * multiplier);
            destRow[i] = invert ? (byte)(255 - v) : v;
        }
    }

    private static void WriteGray16(ReadOnlySpan<ushort> samples, Span<byte> destRow, bool invert)
    {
        var dest = MemoryMarshal.Cast<byte, ushort>(destRow);

        if (!invert)
        {
            samples.CopyTo(dest);
            return;
        }

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= samples.Length; i += 16)
            {
                (~Vector256.LoadUnsafe(in samples[i])).StoreUnsafe(ref dest[i]);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 8 <= samples.Length; i += 8)
            {
                (~Vector128.LoadUnsafe(in samples[i])).StoreUnsafe(ref dest[i]);
            }
        }

        for (; i < samples.Length; i++)
        {
            dest[i] = (ushort)~samples[i];
        }
    }

    /// <summary>Narrows each already-8-bit-range sample to a byte by truncation (not the top-byte-truncation <c>PixelFormatConversionKernels.NarrowUInt16ToBytes</c> does for a genuine 16-&gt;8-bit channel-depth conversion — these samples already hold their final 0-255 value in a wider type, so the low byte alone is the answer). Used for plain RGB (Photometric=2, no palette), CMYK (Photometric=5), and non-premultiplied RGBA's color+alpha channels alike, since all three are a contiguous same-order sample stream once unscaled.</summary>
    private static void NarrowSamplesToBytes(ReadOnlySpan<ushort> samples, Span<byte> destRow)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= samples.Length; i += 32)
            {
                var lo = Vector256.LoadUnsafe(in samples[i]);
                var hi = Vector256.LoadUnsafe(in samples[i + 16]);
                Vector256.Narrow(lo, hi).StoreUnsafe(ref destRow[i]);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= samples.Length; i += 16)
            {
                var lo = Vector128.LoadUnsafe(in samples[i]);
                var hi = Vector128.LoadUnsafe(in samples[i + 8]);
                Vector128.Narrow(lo, hi).StoreUnsafe(ref destRow[i]);
            }
        }

        for (; i < samples.Length; i++)
        {
            destRow[i] = (byte)samples[i];
        }
    }

    private static void WriteRgba32Premultiplied(ReadOnlySpan<ushort> samples, Span<byte> destRow)
    {
        int pixelCount = samples.Length / 4;
        for (int p = 0; p < pixelCount; p++)
        {
            int s = p * 4;
            byte a = (byte)samples[s + 3];

            if (a != 0)
            {
                destRow[s] = (byte)Math.Min(255, (samples[s] * 255) / a);
                destRow[s + 1] = (byte)Math.Min(255, (samples[s + 1] * 255) / a);
                destRow[s + 2] = (byte)Math.Min(255, (samples[s + 2] * 255) / a);
            }
            else
            {
                destRow[s] = (byte)samples[s];
                destRow[s + 1] = (byte)samples[s + 1];
                destRow[s + 2] = (byte)samples[s + 2];
            }

            destRow[s + 3] = a;
        }
    }

    private static void WriteRgba64Premultiplied(ReadOnlySpan<ushort> samples, Span<byte> destRow)
    {
        var dest = MemoryMarshal.Cast<byte, ushort>(destRow);
        int pixelCount = samples.Length / 4;

        for (int p = 0; p < pixelCount; p++)
        {
            int s = p * 4;
            ushort a = samples[s + 3];

            if (a != 0)
            {
                dest[s] = (ushort)Math.Min(65535, (long)samples[s] * 65535 / a);
                dest[s + 1] = (ushort)Math.Min(65535, (long)samples[s + 1] * 65535 / a);
                dest[s + 2] = (ushort)Math.Min(65535, (long)samples[s + 2] * 65535 / a);
            }
            else
            {
                dest[s] = samples[s];
                dest[s + 1] = samples[s + 1];
                dest[s + 2] = samples[s + 2];
            }

            dest[s + 3] = a;
        }
    }

    private static void WritePaletteIndices(ReadOnlySpan<ushort> samples, byte[] palette, Span<byte> destRow)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int paletteOffset = samples[i] * 3;
            int destOffset = i * 3;
            destRow[destOffset] = palette[paletteOffset];
            destRow[destOffset + 1] = palette[paletteOffset + 1];
            destRow[destOffset + 2] = palette[paletteOffset + 2];
        }
    }
}
