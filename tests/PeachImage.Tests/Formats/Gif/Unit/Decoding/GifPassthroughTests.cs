using PeachImage.Formats.Gif;
using PeachImage.Formats.Gif.Decoding;
using PeachImage.Formats.Gif.Encoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Decoding;

/// <summary>
/// <see cref="GifPassthrough.TryRead"/> reads a GIF's first frame's LZW-compressed image data verbatim,
/// with sub-block framing stripped and no LZW decompress, for callers (e.g. PDF <c>/LZWDecode</c>
/// embedding) that want the raw compressed bytes rather than a decoded <see cref="Image"/>. Most tests
/// build real GIFs via <see cref="GifEncoder"/> (so the LZW/palette data is realistic) and cross-check the
/// passthrough result against decompressing <see cref="GifPassthroughInfo.LzwData"/> directly with
/// <see cref="GifLzwDecoder"/> plus <see cref="GifPassthroughInfo.Palette"/>; the interlace test hand-builds
/// a minimal GIF since <see cref="GifEncoder"/> never writes interlaced output.
/// </summary>
public class GifPassthroughTests
{
    [Fact]
    public void OpaqueImage_LzwDataDecompressesToSameIndicesAsFullDecode()
    {
        var source = CreateTiledImage(16, 12, colorCount: 16);
        byte[] file = Encode(source, new GifEncoderOptions());

        bool ok = GifPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.Equal(16, info.Width);
        Assert.Equal(12, info.Height);
        Assert.Equal(0, info.Left);
        Assert.Equal(0, info.Top);
        Assert.Equal(16, info.CanvasWidth);
        Assert.Equal(12, info.CanvasHeight);
        Assert.False(info.Interlaced);
        Assert.False(info.IsAnimated);
        Assert.Null(info.TransparentColorIndex);

        var decoded = GifDecoder.Decode(new MemoryStream(file));
        AssertLzwDataMatchesDecodedPixels(info, decoded, hasAlpha: false);
    }

    [Fact]
    public void SmallPalette_ReportsMinCodeSizeLessThanEight()
    {
        var source = CreateTiledImage(8, 8, colorCount: 4);
        byte[] file = Encode(source, new GifEncoderOptions { MaxColors = 4 });

        bool ok = GifPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.True(info.MinCodeSize < 8, $"Expected a small-palette GIF to use MinCodeSize < 8, got {info.MinCodeSize}.");
    }

    [Fact]
    public void FullPalette_ReportsMinCodeSizeEight()
    {
        // A genuine 256-distinct-color image (unlike CreateTiledImage, whose 3x3 tiling caps out well
        // below 256 actual colors at this size) -- each of the 4096 pixels maps to one of 256 colors via
        // its raster index, guaranteeing every palette entry is actually used.
        var source = Image.Create(64, 64, PixelFormat.Rgb24);
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < 64 * 64; i++)
        {
            int color = i % 256;
            pixels[(i * 3) + 0] = (byte)((color * 53) % 256);
            pixels[(i * 3) + 1] = (byte)((color * 97) % 256);
            pixels[(i * 3) + 2] = (byte)((color * 181) % 256);
        }

        byte[] file = Encode(source, new GifEncoderOptions { MaxColors = 256 });

        bool ok = GifPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.Equal(8, info.MinCodeSize);
    }

    [Fact]
    public void TransparentImage_ReportsTransparentColorIndexMatchingDecodedTransparentPixels()
    {
        var source = CreateImageWithTransparentBorder(24, 16);
        byte[] file = Encode(source, new GifEncoderOptions());

        bool ok = GifPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.NotNull(info.TransparentColorIndex);

        var decoded = GifDecoder.Decode(new MemoryStream(file));
        AssertLzwDataMatchesDecodedPixels(info, decoded, hasAlpha: true);
    }

    [Fact]
    public void AnimatedGif_ReportsIsAnimatedTrueAndFrame1LzwData()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(CreateTiledImage(8, 8, colorCount: 4), TimeSpan.FromMilliseconds(20), FrameDisposalMethod.DoNotDispose),
            new(CreateTiledImage(8, 8, colorCount: 4), TimeSpan.FromMilliseconds(20), FrameDisposalMethod.DoNotDispose),
        };
        var animation = new AnimatedImage(frames, width: 8, height: 8, loopCount: 0);
        using var ms = new MemoryStream();
        animation.Save(ms, "gif", new GifEncoderOptions());
        byte[] file = ms.ToArray();

        bool ok = GifPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.True(info.IsAnimated);

        var firstFrame = GifDecoder.Decode(new MemoryStream(file));
        AssertLzwDataMatchesDecodedPixels(info, firstFrame, hasAlpha: false);
    }

    [Fact]
    public void InterlacedFrame_ReportsInterlacedTrue()
    {
        byte[] file = BuildInterlacedGif();

        bool ok = GifPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.True(info.Interlaced);
    }

    [Fact]
    public void NonGifStream_ReturnsFalse()
    {
        byte[] notAGif = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        bool ok = GifPassthrough.TryRead(new MemoryStream(notAGif), out var info);

        Assert.False(ok);
        Assert.Equal(default, info);
    }

    [Fact]
    public void NoImageFrames_ReturnsFalse()
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        WriteUInt16(ms, 1);
        WriteUInt16(ms, 1);
        ms.WriteByte(0); // no global color table
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0x3B); // trailer, no frames

        bool ok = GifPassthrough.TryRead(new MemoryStream(ms.ToArray()), out _);

        Assert.False(ok);
    }

    /// <summary>Decompresses <paramref name="info"/>'s LZW data, maps it through its reported palette, and compares against <paramref name="decoded"/>'s pixels (skipping transparent-index pixels when <paramref name="hasAlpha"/>, matching how the full decoder treats them).</summary>
    private static void AssertLzwDataMatchesDecodedPixels(GifPassthroughInfo info, Image decoded, bool hasAlpha)
    {
        byte[] indices = GifLzwDecoder.Decode(info.LzwData, info.MinCodeSize, info.Width * info.Height);
        var expected = decoded.GetPixelSpan();
        int bytesPerPixel = hasAlpha ? 4 : 3;

        for (int y = 0; y < info.Height; y++)
        {
            for (int x = 0; x < info.Width; x++)
            {
                int index = indices[(y * info.Width) + x];
                int destOffset = (((info.Top + y) * decoded.Width) + (info.Left + x)) * bytesPerPixel;

                if (hasAlpha && info.TransparentColorIndex == index)
                {
                    Assert.Equal(0, expected[destOffset + 3]);
                    continue;
                }

                int paletteOffset = index * 3;
                Assert.Equal(info.Palette[paletteOffset], expected[destOffset]);
                Assert.Equal(info.Palette[paletteOffset + 1], expected[destOffset + 1]);
                Assert.Equal(info.Palette[paletteOffset + 2], expected[destOffset + 2]);
            }
        }
    }

    private static byte[] Encode(Image image, GifEncoderOptions options)
    {
        using var ms = new MemoryStream();
        GifEncoder.Encode(image, ms, options);
        return ms.ToArray();
    }

    private static Image CreateTiledImage(int width, int height, int colorCount)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int tile = ((x / 3) + (y / 3)) % colorCount;
                row[(x * 3) + 0] = (byte)((tile * 53) % 256);
                row[(x * 3) + 1] = (byte)((tile * 97) % 256);
                row[(x * 3) + 2] = (byte)((tile * 181) % 256);
            }
        }

        return image;
    }

    private static Image CreateImageWithTransparentBorder(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                bool border = x < 3 || y < 3 || x >= width - 3 || y >= height - 3;
                if (border)
                {
                    row.Slice(x * 4, 4).Clear();
                }
                else
                {
                    row[(x * 4) + 0] = (byte)(x % 7 * 30);
                    row[(x * 4) + 1] = (byte)(y % 5 * 40);
                    row[(x * 4) + 2] = 128;
                    row[(x * 4) + 3] = 255;
                }
            }
        }

        return image;
    }

    private static byte[] BuildInterlacedGif()
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        WriteUInt16(ms, 2);
        WriteUInt16(ms, 2);
        ms.WriteByte(0x80); // global color table flag, size code 0 -> 2 entries
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(255); ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte(0); ms.WriteByte(255); ms.WriteByte(0);

        ms.WriteByte(0x2C); // image separator
        WriteUInt16(ms, 0);
        WriteUInt16(ms, 0);
        WriteUInt16(ms, 2);
        WriteUInt16(ms, 2);
        ms.WriteByte(0x40); // packed: no local color table, interlace flag set

        byte[] indices = [0, 1, 1, 0];
        const int minCodeSize = 2;
        ms.WriteByte(minCodeSize);
        GifLzwEncoder.Encode(ms, indices, minCodeSize);

        ms.WriteByte(0x3B); // trailer
        return ms.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
