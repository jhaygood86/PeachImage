using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding;
using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// End-to-end smoke tests for <see cref="Vp8ImageEncoder"/>: validates that the container/header/partition
/// framing it produces is a real, spec-valid VP8 keyframe the unmodified <see cref="Vp8FrameDecoder"/> can open,
/// across a range of macroblock-aligned and non-aligned dimensions. Only dimensions/format are asserted here,
/// not pixel fidelity -- see <see cref="Vp8FrameEncoderIntegrationTests"/> for PSNR-based reconstruction-quality
/// assertions.
/// </summary>
public class Vp8ImageEncoderIntegrationTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(15, 17)]
    [InlineData(33, 20)]
    [InlineData(64, 48)]
    public void Encode_ProducesBitstreamTheRealDecoderCanOpen(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];
        new Random(1).NextBytes(rgb);

        byte[] vp8Bytes = Vp8ImageEncoder.Encode(rgb, width, height, new WebpEncoderOptions { Lossless = false });

        Vp8DecodedFrame frame = Vp8FrameDecoder.Instance.Decode(vp8Bytes, null);

        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(PixelFormat.Rgb24, frame.PixelFormat);
        Assert.Equal(width * height * 3, frame.Pixels.Length);
    }

    [Fact]
    public void Encode_StartsWithValidKeyframeStartCode()
    {
        byte[] rgb = new byte[16 * 16 * 3];
        byte[] vp8Bytes = Vp8ImageEncoder.Encode(rgb, 16, 16, new WebpEncoderOptions { Lossless = false });

        Assert.Equal(0x9d, vp8Bytes[3]);
        Assert.Equal(0x01, vp8Bytes[4]);
        Assert.Equal(0x2a, vp8Bytes[5]);
    }

    [Fact]
    public void Encode_LargerImage_HasMoreMacroblocksButStillDecodes()
    {
        const int width = 160;
        const int height = 96;
        byte[] rgb = new byte[width * height * 3];

        byte[] vp8Bytes = Vp8ImageEncoder.Encode(rgb, width, height, new WebpEncoderOptions { Lossless = false });
        Vp8DecodedFrame frame = Vp8FrameDecoder.Instance.Decode(vp8Bytes, null);

        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
    }
}
