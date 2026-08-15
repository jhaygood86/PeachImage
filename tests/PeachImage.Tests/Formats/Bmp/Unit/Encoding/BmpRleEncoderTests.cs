using PeachImage.Formats.Bmp.Decoding;
using PeachImage.Formats.Bmp.Encoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Encoding;

public class BmpRleEncoderTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(16, 16)]
    [InlineData(7, 3)] // Non-multiple-of-run-length dimensions.
    public void EncodeRle8_RoundTripsThroughDecoder(int width, int height)
    {
        byte[] pixels = new byte[width * height];
        var random = new Random(42);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)random.Next(0, 4); // Small value range so runs actually form.
        }

        byte[] compressed = BmpRleEncoder.EncodeRle8(pixels, width, height);
        byte[] decoded = BmpRleDecoder.DecodeRle8(compressed, width, height);

        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void EncodeRle8_UniformImage_RoundTrips()
    {
        byte[] pixels = Enumerable.Repeat((byte)42, 10 * 10).ToArray();

        byte[] compressed = BmpRleEncoder.EncodeRle8(pixels, width: 10, height: 10);
        byte[] decoded = BmpRleDecoder.DecodeRle8(compressed, width: 10, height: 10);

        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void EncodeRle8_CapsRunLengthAtByteMax()
    {
        // A run of 300 identical pixels must be split into multiple (count,value) pairs, since count is a
        // single byte (max 255) — verify the round trip still reproduces every pixel correctly.
        byte[] pixels = Enumerable.Repeat((byte)7, 300).ToArray();

        byte[] compressed = BmpRleEncoder.EncodeRle8(pixels, width: 300, height: 1);
        byte[] decoded = BmpRleDecoder.DecodeRle8(compressed, width: 300, height: 1);

        Assert.Equal(pixels, decoded);
    }
}
