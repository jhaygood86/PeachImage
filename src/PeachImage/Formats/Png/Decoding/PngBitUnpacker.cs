using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>
/// Unpacks a defiltered, still bit-depth-packed scanline into per-pixel samples. Always widens into
/// <see cref="ushort"/> regardless of source bit depth — a small, deliberate simplification over
/// separate 8-bit/16-bit code paths, trading a little unused range for one uniform, easy-to-verify
/// implementation. For color type 3 (palette), the "samples" are raw, unscaled palette indices; for
/// every other color type, they are raw, unscaled sample magnitudes (0..2^bitDepth-1) — critically,
/// NOT yet scaled to an 8-bit display range, since tRNS-key comparison must happen against the
/// unscaled value (spec §11.3.2.1).
/// </summary>
internal static class PngBitUnpacker
{
    /// <summary>Unpacks <paramref name="packedRow"/> (bit depth 1/2/4/8/16) into <paramref name="samplesOut"/> (length must be <paramref name="pixelCount"/> * samplesPerPixel).</summary>
    public static void Unpack(ReadOnlySpan<byte> packedRow, int bitDepth, int pixelCount, int samplesPerPixel, Span<ushort> samplesOut)
    {
        int sampleCount = pixelCount * samplesPerPixel;

        switch (bitDepth)
        {
            case 16:
                for (int i = 0; i < sampleCount; i++)
                {
                    samplesOut[i] = BinaryPrimitives.ReadUInt16BigEndian(packedRow.Slice(i * 2, 2));
                }

                return;

            case 8:
                {
                    int i = 0;
                    if (Vector128.IsHardwareAccelerated && sampleCount >= Vector128<byte>.Count)
                    {
                        _ = packedRow[sampleCount - 1];
                        _ = samplesOut[sampleCount - 1];
                        ref byte src = ref MemoryMarshal.GetReference(packedRow);
                        ref ushort dst = ref MemoryMarshal.GetReference(samplesOut);
                        for (; i + Vector128<byte>.Count <= sampleCount; i += Vector128<byte>.Count)
                        {
                            var bytes = Vector128.LoadUnsafe(ref src, (nuint)i);
                            Vector128.WidenLower(bytes).StoreUnsafe(ref dst, (nuint)i);
                            Vector128.WidenUpper(bytes).StoreUnsafe(ref dst, (nuint)(i + (Vector128<byte>.Count / 2)));
                        }
                    }

                    for (; i < sampleCount; i++)
                    {
                        samplesOut[i] = packedRow[i];
                    }

                    return;
                }

            case 4:
            case 2:
            case 1:
                {
                    // Each source byte holds 8 / bitDepth samples, most significant first; walk them with a
                    // shift per sample rather than dividing the sample index by the samples-per-byte count.
                    byte mask = (byte)((1 << bitDepth) - 1);
                    int i = 0;
                    for (int byteIndex = 0; i < sampleCount; byteIndex++)
                    {
                        int packed = packedRow[byteIndex];
                        for (int shift = 8 - bitDepth; shift >= 0 && i < sampleCount; shift -= bitDepth)
                        {
                            samplesOut[i++] = (ushort)((packed >> shift) & mask);
                        }
                    }
                }

                return;

            default:
                throw new PngDecodingException($"Unsupported PNG bit depth {bitDepth}.");
        }
    }
}
