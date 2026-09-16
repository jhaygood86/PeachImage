using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// <see cref="ImageInfo.IsLosslessEncoding"/> for AVIF: derived from every AV1 color tile's frame header
/// reporting <c>AllLossless</c> (identity-transform, qindex/delta-q all zero, no superres). Uses real
/// encoded AVIF files (via <see cref="AvifEncoder"/>), not <see cref="AvifFixtureBuilder"/>'s dummy AV1
/// tile bytes — <see cref="AvifDecoder.Identify"/> now parses the AV1 frame header to compute this, which
/// a dummy/synthetic tile payload can't satisfy (see <see cref="AvifDecoderTests.Decode_DummyTilePayload_ThrowsAvifFormatException"/>
/// for why dummy tiles are only ever valid for container-structure tests).
/// </summary>
public class AvifIsLosslessEncodingTests
{
    [Fact]
    public void LosslessEncode_ReportsLosslessTrue()
    {
        var source = CreateSolidColorImage(16, 16);
        var info = EncodeThenIdentify(source, new AvifEncoderOptions { Lossless = true });

        Assert.True(info.IsLosslessEncoding);
    }

    [Fact]
    public void LossyEncode_ReportsLosslessFalse()
    {
        var source = CreateSolidColorImage(16, 16);
        var info = EncodeThenIdentify(source, new AvifEncoderOptions { Lossless = false, Quality = 75 });

        Assert.False(info.IsLosslessEncoding);
    }

    private static ImageInfo EncodeThenIdentify(Image source, AvifEncoderOptions options)
    {
        using var ms = new MemoryStream();
        source.Save(ms, "avif", options);
        ms.Position = 0;
        return Image.Identify(ms);
    }

    private static Image CreateSolidColorImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 180;
            pixels[i + 1] = 90;
            pixels[i + 2] = 40;
        }

        return image;
    }
}
