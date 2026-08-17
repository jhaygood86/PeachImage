namespace PeachImage.Tests;

/// <summary>
/// Pins the guarantee documented on <see cref="DecoderOptions.TargetPixelFormat"/>: a caller that doesn't
/// know a stream's format up front (the whole point of <see cref="Image.Load(Stream, DecoderOptions?)"/>'s
/// format sniffing) can construct a single, format-agnostic <c>new DecoderOptions { TargetPixelFormat = ... }</c>
/// — no per-format subtype guessing required — and every codec honors it identically. Covers every format
/// with an encoder (Jpeg/Png/Bmp/Gif/Webp); AVIF is decode-only, but <c>AvifDecoder.Decode</c> reads
/// <c>options?.TargetPixelFormat</c> off the same base-typed parameter as the rest, confirmed by inspection.
/// </summary>
public class DecoderOptionsFormatAgnosticTests
{
    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    public void PlainDecoderOptions_RequestingRgba32_IsHonoredRegardlessOfDetectedFormat(string formatName)
    {
        using var source = CreateGradientRgbImage(16, 12);
        using var stream = new MemoryStream();
        source.Save(stream, formatName);
        stream.Position = 0;

        // The base type, not JpegDecoderOptions/PngDecoderOptions/etc — this is the point being tested.
        using var decoded = Image.Load(stream, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }

    [Fact]
    public void PlainDecoderOptions_BehavesIdenticallyToTheMatchingConcreteSubtype()
    {
        using var source = CreateGradientRgbImage(10, 8);
        using var jpegStream = new MemoryStream();
        source.Save(jpegStream, "jpeg");
        jpegStream.Position = 0;

        using var viaBase = Image.Load(jpegStream, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });
        jpegStream.Position = 0;
        using var viaConcrete = Image.Load(jpegStream, new PeachImage.Formats.Jpeg.JpegDecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.Equal(viaConcrete.PixelFormat, viaBase.PixelFormat);
        Assert.True(viaConcrete.GetPixelSpan().SequenceEqual(viaBase.GetPixelSpan()));
    }

    private static Image CreateGradientRgbImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 3) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 3) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 3) + 2] = (byte)((x + y) % 256);
            }
        }

        return image;
    }
}
