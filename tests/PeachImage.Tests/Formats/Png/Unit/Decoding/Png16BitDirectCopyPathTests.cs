using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Decoding;

/// <summary>
/// Decodes real 16-bit-truecolor PNG bytes through <see cref="PngDecoder"/>'s <c>directCopy16</c> fast
/// path (the whole point of <see cref="PeachImage.Formats.Png.Decoding.Png16BitByteSwap"/>), rather than
/// calling the byte-swap helper directly — so a wiring mistake at the call site would still be caught even
/// if the helper's own unit tests pass.
/// </summary>
public class Png16BitDirectCopyPathTests
{
    // Width 7 -> 7 * 3 samples * 2 bytes = 42 bytes/row, not a multiple of 16, forcing the SIMD kernel's
    // scalar tail (42 % 16 = 10 bytes / 5 samples) to run through the real decode path.
    [Fact]
    public void TruecolorRow16Bit_NonVectorWidthMultiple_DecodesExactSampleValues()
    {
        const int width = 7;
        ushort[] expectedSamples = new ushort[width * 3];
        var rowBytes = new byte[1 + (width * 3 * 2)];
        rowBytes[0] = 0; // filter type None

        uint state = 0x1234_5678;
        for (int i = 0; i < expectedSamples.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            ushort sample = (ushort)state;
            expectedSamples[i] = sample;
            BinaryPrimitives.WriteUInt16BigEndian(rowBytes.AsSpan(1 + (i * 2), 2), sample);
        }

        byte[] file = PngTestFileBuilder.Build(
            width: width,
            height: 1,
            bitDepth: 16,
            colorType: 2,
            palette: null,
            trns: null,
            scanlines: [rowBytes]);

        using var image = PngDecoder.Decode(new MemoryStream(file));

        Assert.Equal(PixelFormat.Rgb48, image.PixelFormat);
        var decodedSamples = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        Assert.Equal(expectedSamples, decodedSamples.ToArray());
    }

    [Fact]
    public void TruecolorRow16Bit_MultipleRowsAndVectorWidthMultiple_DecodesExactSampleValues()
    {
        // Width 8 -> 48 bytes/row, three full SIMD vectors, no scalar tail; multiple rows exercises the
        // previous/current row buffer swap alongside the fast path.
        const int width = 8;
        const int height = 3;
        var scanlines = new List<byte[]>();
        var expectedSamples = new ushort[width * 3 * height];
        uint state = 0xABCD_EF01;
        int sampleIndex = 0;

        for (int y = 0; y < height; y++)
        {
            var rowBytes = new byte[1 + (width * 3 * 2)];
            rowBytes[0] = 0; // filter type None
            for (int i = 0; i < width * 3; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                ushort sample = (ushort)state;
                expectedSamples[sampleIndex++] = sample;
                BinaryPrimitives.WriteUInt16BigEndian(rowBytes.AsSpan(1 + (i * 2), 2), sample);
            }

            scanlines.Add(rowBytes);
        }

        byte[] file = PngTestFileBuilder.Build(
            width: width,
            height: height,
            bitDepth: 16,
            colorType: 2,
            palette: null,
            trns: null,
            scanlines: scanlines);

        using var image = PngDecoder.Decode(new MemoryStream(file));

        var decodedSamples = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        Assert.Equal(expectedSamples, decodedSamples.ToArray());
    }
}
