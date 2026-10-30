using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Decoding;

public class ExtendedSequentialJpegTests
{
    [Fact]
    public void Sof1_EightBitHuffman_DecodesSameAsSof0()
    {
        byte[] baseline = EncodeJpeg();
        byte[] extended = (byte[])baseline.Clone();
        int sof = IndexOfSof0(extended);
        extended[sof + 1] = 0xC1;

        using var expected = Image.Load(new MemoryStream(baseline));
        using var actual = Image.Load(new MemoryStream(extended));

        Assert.Equal(expected.GetPixelSpan().ToArray(), actual.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Sof1_TwelveBitPrecision_IsRejected()
    {
        byte[] extended = EncodeJpeg();
        int sof = IndexOfSof0(extended);
        extended[sof + 1] = 0xC1;
        extended[sof + 4] = 12; // sample precision

        Assert.ThrowsAny<Exception>(() => Image.Load(new MemoryStream(extended)));
    }

    private static int IndexOfSof0(byte[] jpeg)
    {
        for (int i = 0; i < jpeg.Length - 1; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("No SOF0 marker found.");
    }

    private static byte[] EncodeJpeg()
    {
        using var source = Image.Create(16, 16, PixelFormat.Rgb24);
        for (int y = 0; y < 16; y++)
        {
            var row = source.GetRowSpan(y);
            for (int x = 0; x < 16; x++)
            {
                row[(x * 3) + 0] = (byte)(x * 16);
                row[(x * 3) + 1] = (byte)(y * 16);
                row[(x * 3) + 2] = 128;
            }
        }

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90 });
        return ms.ToArray();
    }
}
