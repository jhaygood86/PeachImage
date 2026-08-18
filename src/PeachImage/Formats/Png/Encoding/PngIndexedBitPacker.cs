namespace PeachImage.Formats.Png.Encoding;

/// <summary>Packs one row of per-pixel palette-index bytes (values 0..255, using only the low <c>bitDepth</c> bits) into PNG's bit-depth-packed scanline layout (spec §7.2): samples fill each byte MSB-first, with no padding between samples.</summary>
internal static class PngIndexedBitPacker
{
    public static void PackRow(ReadOnlySpan<byte> indices, int bitDepth, Span<byte> destination)
    {
        if (bitDepth == 8)
        {
            indices.CopyTo(destination);
            return;
        }

        destination.Clear();
        int samplesPerByte = 8 / bitDepth;
        for (int i = 0; i < indices.Length; i++)
        {
            int byteIndex = i / samplesPerByte;
            int sampleInByte = i % samplesPerByte;
            int shift = 8 - bitDepth - (sampleInByte * bitDepth);
            destination[byteIndex] |= (byte)(indices[i] << shift);
        }
    }
}
