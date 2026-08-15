using System.Buffers.Binary;

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
                for (int i = 0; i < sampleCount; i++)
                {
                    samplesOut[i] = packedRow[i];
                }

                return;

            case 4:
            case 2:
            case 1:
                int samplesPerByte = 8 / bitDepth;
                byte mask = (byte)((1 << bitDepth) - 1);
                for (int i = 0; i < sampleCount; i++)
                {
                    int byteIndex = i / samplesPerByte;
                    int sampleInByte = i % samplesPerByte;
                    int shift = 8 - bitDepth - (sampleInByte * bitDepth);
                    samplesOut[i] = (ushort)((packedRow[byteIndex] >> shift) & mask);
                }

                return;

            default:
                throw new PngDecodingException($"Unsupported PNG bit depth {bitDepth}.");
        }
    }
}
