using PeachImage.Formats.Webp.Decoding;

namespace PeachImage.Tests.Formats.Webp.Unit.Decoding;

public class WebpBitstreamHeaderPeekTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(16384, 16384, true)]
    [InlineData(320, 240, false)]
    [InlineData(1, 16384, true)]
    public void TryPeekVp8L_ExtractsWidthHeightAlpha(int width, int height, bool alphaIsUsed)
    {
        byte[] data = BuildVp8LHeader(width, height, alphaIsUsed);

        bool ok = WebpBitstreamHeaderPeek.TryPeekVp8L(data, out int peekedWidth, out int peekedHeight, out bool peekedAlpha);

        Assert.True(ok);
        Assert.Equal(width, peekedWidth);
        Assert.Equal(height, peekedHeight);
        Assert.Equal(alphaIsUsed, peekedAlpha);
    }

    [Fact]
    public void TryPeekVp8L_WrongSignature_ReturnsFalse()
    {
        byte[] data = BuildVp8LHeader(10, 10, false);
        data[0] = 0x00;

        Assert.False(WebpBitstreamHeaderPeek.TryPeekVp8L(data, out _, out _, out _));
    }

    [Fact]
    public void TryPeekVp8L_TooShort_ReturnsFalse()
    {
        Assert.False(WebpBitstreamHeaderPeek.TryPeekVp8L([0x2F, 0x01], out _, out _, out _));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16383, 16383)]
    [InlineData(1920, 1080)]
    public void TryPeekVp8_ExtractsWidthHeight(int width, int height)
    {
        byte[] data = BuildVp8Header(width, height);

        bool ok = WebpBitstreamHeaderPeek.TryPeekVp8(data, out int peekedWidth, out int peekedHeight);

        Assert.True(ok);
        Assert.Equal(width, peekedWidth);
        Assert.Equal(height, peekedHeight);
    }

    [Fact]
    public void TryPeekVp8_WrongStartCode_ReturnsFalse()
    {
        byte[] data = BuildVp8Header(100, 100);
        data[3] = 0x00;

        Assert.False(WebpBitstreamHeaderPeek.TryPeekVp8(data, out _, out _));
    }

    private static byte[] BuildVp8LHeader(int width, int height, bool alphaIsUsed)
    {
        uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14) | (alphaIsUsed ? 1u << 28 : 0u);
        return
        [
            0x2F,
            (byte)(bits & 0xFF),
            (byte)((bits >> 8) & 0xFF),
            (byte)((bits >> 16) & 0xFF),
            (byte)((bits >> 24) & 0xFF),
        ];
    }

    private static byte[] BuildVp8Header(int width, int height)
    {
        byte[] data = new byte[10];
        // bytes 0..3: uncompressed frame tag (arbitrary for this test — not interpreted by TryPeekVp8).
        data[0] = 0x00;
        data[1] = 0x00;
        data[2] = 0x00;
        data[3] = 0x9d;
        data[4] = 0x01;
        data[5] = 0x2a;
        data[6] = (byte)(width & 0xFF);
        data[7] = (byte)((width >> 8) & 0x3F);
        data[8] = (byte)(height & 0xFF);
        data[9] = (byte)((height >> 8) & 0x3F);
        return data;
    }
}
