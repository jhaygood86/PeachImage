using System.Buffers.Binary;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Encoding;

public class PngIndexedEncodeTests
{
    [Fact]
    public void Auto_LowColorRgb24Source_EncodesIndexedAndRoundTripsExactly()
    {
        using var source = CreatePaletteImage(width: 16, height: 16, colorCount: 5);

        byte[] encoded = Encode(source, new PngEncoderOptions());

        Assert.Equal(3, FindColorType(encoded));
        using var decoded = PngDecoder.Decode(new MemoryStream(encoded));
        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Auto_HighColorRgb24Source_FallsBackToTruecolor()
    {
        using var source = CreateGradientImage(64, 48); // Far more than 256 distinct colors.

        byte[] encoded = Encode(source, new PngEncoderOptions());

        Assert.Equal(2, FindColorType(encoded));
        Assert.False(HasChunk(encoded, "PLTE"));
        using var decoded = PngDecoder.Decode(new MemoryStream(encoded));
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Auto_BinaryAlphaLowColorSource_EncodesIndexedWithPaletteTransparency()
    {
        const int transparentPixelIndex = 5;
        using var source = CreatePaletteImageWithBinaryAlpha(width: 8, height: 8, colorCount: 3, transparentPixelIndex);

        byte[] encoded = Encode(source, new PngEncoderOptions());

        Assert.Equal(3, FindColorType(encoded));
        Assert.True(HasChunk(encoded, "tRNS"));
        using var decoded = PngDecoder.Decode(new MemoryStream(encoded));
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);

        // Indexed transparency (mirroring GIF) reserves one dedicated index purely to mean "transparent",
        // distinct from any opaque pixel's color index — otherwise an opaque pixel sharing that index would
        // incorrectly become transparent too. The tradeoff: the original RGB a transparent pixel had is not
        // preserved, only its alpha; every opaque pixel's full RGBA is still exact.
        var sourceSpan = source.GetPixelSpan();
        var decodedSpan = decoded.GetPixelSpan();
        for (int i = 0; i < source.Width * source.Height; i++)
        {
            if (i == transparentPixelIndex)
            {
                Assert.Equal(0, decodedSpan[(i * 4) + 3]);
                continue;
            }

            Assert.Equal(sourceSpan.Slice(i * 4, 4).ToArray(), decodedSpan.Slice(i * 4, 4).ToArray());
        }
    }

    [Fact]
    public void Auto_PartialAlphaSource_NeverIndexes_PreservesAlphaExactly()
    {
        // Every pixel a distinct low-count color, but alpha ramps through intermediate values — indexed
        // color's binary per-entry transparency can't represent that losslessly, so Auto must decline.
        using var source = Image.Create(8, 8, PixelFormat.Rgba32);
        var span = source.GetPixelSpan();
        for (int i = 0; i < 64; i++)
        {
            span[(i * 4) + 0] = 10;
            span[(i * 4) + 1] = 20;
            span[(i * 4) + 2] = 30;
            span[(i * 4) + 3] = (byte)(i * 4);
        }

        byte[] encoded = Encode(source, new PngEncoderOptions());

        Assert.Equal(6, FindColorType(encoded));
        using var decoded = PngDecoder.Decode(new MemoryStream(encoded));
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void ColorModeTruecolor_NeverIndexesEvenLowColorSource()
    {
        using var source = CreatePaletteImage(width: 4, height: 4, colorCount: 2);

        byte[] encoded = Encode(source, new PngEncoderOptions { ColorMode = PngColorMode.Truecolor });

        Assert.Equal(2, FindColorType(encoded));
        Assert.False(HasChunk(encoded, "PLTE"));
    }

    [Fact]
    public void ColorModeIndexed_QuantizesHighColorSource_ToAtMostMaxColors()
    {
        using var source = CreateGradientImage(64, 48);

        byte[] encoded = Encode(source, new PngEncoderOptions { ColorMode = PngColorMode.Indexed, MaxColors = 32 });

        Assert.Equal(3, FindColorType(encoded));
        int? plteLength = FindChunkLength(encoded, "PLTE");
        Assert.NotNull(plteLength);
        Assert.True(plteLength / 3 <= 32);

        using var decoded = PngDecoder.Decode(new MemoryStream(encoded));
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }

    [Fact]
    public void ColorModeIndexed_WithDither_StillDecodesToSameDimensions()
    {
        using var source = CreateGradientImage(32, 32);

        byte[] encoded = Encode(source, new PngEncoderOptions { ColorMode = PngColorMode.Indexed, MaxColors = 16, Dither = true });

        Assert.Equal(3, FindColorType(encoded));
        using var decoded = PngDecoder.Decode(new MemoryStream(encoded));
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }

    [Fact]
    public void TransparentColorOption_SkipsIndexedEncodingEvenWhenLowColor()
    {
        using var source = CreatePaletteImage(width: 4, height: 4, colorCount: 2);

        byte[] encoded = Encode(source, new PngEncoderOptions { TransparentColor = (source.GetPixelSpan()[0], source.GetPixelSpan()[1], source.GetPixelSpan()[2]) });

        Assert.Equal(2, FindColorType(encoded));
        Assert.False(HasChunk(encoded, "PLTE"));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(16, 4)]
    [InlineData(17, 8)]
    [InlineData(256, 8)]
    public void Auto_ChoosesSmallestSufficientBitDepthForColorCount(int colorCount, byte expectedBitDepth)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(colorCount));
        using var source = CreatePaletteImage(side, side, colorCount);

        byte[] encoded = Encode(source, new PngEncoderOptions());

        Assert.Equal(3, FindColorType(encoded));
        Assert.Equal(expectedBitDepth, FindBitDepth(encoded));
    }

    [Fact]
    public void Interlaced_LowColorSource_StillRoundTripsExactly()
    {
        using var source = CreatePaletteImage(width: 13, height: 9, colorCount: 6);

        byte[] encoded = Encode(source, new PngEncoderOptions { Interlace = true });

        Assert.Equal(3, FindColorType(encoded));
        using var decoded = PngDecoder.Decode(new MemoryStream(encoded));
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    private static byte[] Encode(Image image, PngEncoderOptions options)
    {
        using var ms = new MemoryStream();
        PngEncoder.Encode(image, ms, options);
        return ms.ToArray();
    }

    private static Image CreatePaletteImage(int width, int height, int colorCount)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var span = image.GetPixelSpan();
        int pixelCount = width * height;
        for (int i = 0; i < pixelCount; i++)
        {
            int colorIndex = i % colorCount;
            span[(i * 3) + 0] = (byte)(colorIndex * 40);
            span[(i * 3) + 1] = (byte)(colorIndex * 17);
            span[(i * 3) + 2] = (byte)(colorIndex * 53);
        }

        return image;
    }

    private static Image CreatePaletteImageWithBinaryAlpha(int width, int height, int colorCount, int transparentPixelIndex)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        var span = image.GetPixelSpan();
        int pixelCount = width * height;
        for (int i = 0; i < pixelCount; i++)
        {
            int colorIndex = i % colorCount;
            span[(i * 4) + 0] = (byte)(colorIndex * 60);
            span[(i * 4) + 1] = (byte)(colorIndex * 30);
            span[(i * 4) + 2] = (byte)(colorIndex * 90);
            span[(i * 4) + 3] = i == transparentPixelIndex ? (byte)0 : (byte)255;
        }

        return image;
    }

    private static Image CreateGradientImage(int width, int height)
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

    private static byte FindColorType(byte[] png) => ReadIhdr(png)[9];

    private static byte FindBitDepth(byte[] png) => ReadIhdr(png)[8];

    private static byte[] ReadIhdr(byte[] png)
    {
        foreach (var (type, data) in EnumerateChunks(png))
        {
            if (type == "IHDR")
            {
                return data;
            }
        }

        throw new InvalidOperationException("No IHDR chunk found.");
    }

    private static bool HasChunk(byte[] png, string type) => EnumerateChunks(png).Any(c => c.Type == type);

    private static int? FindChunkLength(byte[] png, string type)
    {
        foreach (var (chunkType, data) in EnumerateChunks(png))
        {
            if (chunkType == type)
            {
                return data.Length;
            }
        }

        return null;
    }

    private static IEnumerable<(string Type, byte[] Data)> EnumerateChunks(byte[] png)
    {
        int offset = 8; // skip signature
        while (offset < png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            byte[] data = png[(offset + 8)..(offset + 8 + (int)length)];
            yield return (type, data);

            offset += 8 + (int)length + 4; // length + type + data + crc
            if (type == "IEND")
            {
                yield break;
            }
        }
    }
}
