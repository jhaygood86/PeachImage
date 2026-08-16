using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Palette;

public class PaletteTransparencyTests
{
    [Fact]
    public void PaletteWithPartialTrnsArray_ResolvesAlphaPerEntry_RemainingImplicitlyOpaque()
    {
        byte[] palette =
        [
            255, 0, 0, // index 0: red
            0, 255, 0, // index 1: green
            0, 0, 255, // index 2: blue
            255, 255, 0, // index 3: yellow
        ];
        byte[] trns = [0, 128]; // only indices 0 and 1 have explicit alpha; 2 and 3 default to opaque.

        byte[] file = PngTestFileBuilder.Build(
            width: 2,
            height: 2,
            bitDepth: 8,
            colorType: 3,
            palette,
            trns,
            scanlines:
            [
                [0, 0, 1], // filter type None, indices [0, 1]
                [0, 2, 3], // filter type None, indices [2, 3]
            ]);

        using var image = PngDecoder.Decode(new MemoryStream(file));

        Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
        var pixels = image.GetPixelSpan();

        AssertPixel(pixels, 0, (255, 0, 0, 0));
        AssertPixel(pixels, 1, (0, 255, 0, 128));
        AssertPixel(pixels, 2, (0, 0, 255, 255));
        AssertPixel(pixels, 3, (255, 255, 0, 255));
    }

    [Fact]
    public void PaletteWithoutTrns_ResolvesToRgb24()
    {
        byte[] palette =
        [
            10, 20, 30,
            40, 50, 60,
        ];

        byte[] file = PngTestFileBuilder.Build(
            width: 2,
            height: 1,
            bitDepth: 1,
            colorType: 3,
            palette,
            trns: null,
            scanlines: [[0, 0b0100_0000]]); // depth-1 packed: pixel0=index0, pixel1=index1, MSB-first.

        using var image = PngDecoder.Decode(new MemoryStream(file));

        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
        var pixels = image.GetPixelSpan();
        Assert.Equal(10, pixels[0]);
        Assert.Equal(20, pixels[1]);
        Assert.Equal(30, pixels[2]);
        Assert.Equal(40, pixels[3]);
        Assert.Equal(50, pixels[4]);
        Assert.Equal(60, pixels[5]);
    }

    [Fact]
    public void PaletteIndexOutOfRange_Throws()
    {
        byte[] palette = [10, 20, 30];

        byte[] file = PngTestFileBuilder.Build(
            width: 1,
            height: 1,
            bitDepth: 8,
            colorType: 3,
            palette,
            trns: null,
            scanlines: [[0, 5]]); // index 5, but palette has only 1 entry.

        Assert.Throws<PngDecodingException>(() => PngDecoder.Decode(new MemoryStream(file)));
    }

    private static void AssertPixel(ReadOnlySpan<byte> pixels, int index, (byte R, byte G, byte B, byte A) expected)
    {
        int o = index * 4;
        Assert.Equal(expected.R, pixels[o]);
        Assert.Equal(expected.G, pixels[o + 1]);
        Assert.Equal(expected.B, pixels[o + 2]);
        Assert.Equal(expected.A, pixels[o + 3]);
    }
}
