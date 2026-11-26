using System.Runtime.InteropServices;
using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.RoundTrip;

public class HighBitDepthRoundTripTests
{
    private static Image Source(PixelFormat format, int bitDepth, int width = 70, int height = 45)
    {
        int channels = format.GetChannelCount();
        var image = Image.Create(width, height, format);
        var samples = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        int max = (1 << bitDepth) - 1;
        var random = new Random(bitDepth);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int value = c switch
                    {
                        0 => x * max / (width - 1),
                        1 => y * max / (height - 1),
                        2 => ((x / 6) + (y / 6)) % 2 == 0 ? max * 3 / 4 : max / 5,
                        _ => max - (x * max / (2 * width)),
                    };
                    value = Math.Clamp(value + random.Next(-6, 7), 0, max);
                    // The same proportional scaling the decoder applies on the way out.
                    samples[(((y * width) + x) * channels) + c] = (ushort)(((value * 65535L) + (max / 2)) / max);
                }
            }
        }

        return image;
    }

    private static byte[] Encode(Image image, AvifEncoderOptions options)
    {
        using var ms = new MemoryStream();
        image.Save(ms, "avif", options);
        return ms.ToArray();
    }

    private static double Psnr(Image a, Image b)
    {
        var x = MemoryMarshal.Cast<byte, ushort>(a.GetPixelSpan());
        var y = MemoryMarshal.Cast<byte, ushort>(b.GetPixelSpan());
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double d = (x[i] - y[i]) / 65535.0;
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(1.0 / (sum / x.Length));
    }

    [Theory]
    [InlineData(PixelFormat.Rgb48, 10)]
    [InlineData(PixelFormat.Rgb48, 12)]
    [InlineData(PixelFormat.Gray16, 10)]
    [InlineData(PixelFormat.Gray16, 12)]
    [InlineData(PixelFormat.Rgba64, 10)]
    [InlineData(PixelFormat.Rgba64, 12)]
    public void Lossless_RoundTripsEverySampleExactly(PixelFormat format, int bitDepth)
    {
        using var source = Source(format, bitDepth);

        var bytes = Encode(source, new AvifEncoderOptions { Lossless = true, BitDepth = bitDepth, Effort = 0 });
        using var back = Image.Load(bytes);
        Assert.Equal(format, back.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void Lossy_ReproducesTheImageClosely_AndBeatsEightBitPrecision(int bitDepth)
    {
        using var source = Source(PixelFormat.Rgb48, bitDepth, 96, 64);
        var bytes = Encode(source, new AvifEncoderOptions { Quality = 90, BitDepth = bitDepth });
        using var back = Image.Load(bytes);
        Assert.Equal(PixelFormat.Rgb48, back.PixelFormat);
        Assert.True(Psnr(source, back) > 38, $"PSNR {Psnr(source, back):F1} dB");
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(12, 2)]
    public void Lossy_WithTileRows_ReproducesTheImage(int bitDepth, int tileRowsLog2)
    {
        using var source = Source(PixelFormat.Rgb48, bitDepth, 128, 512);
        var bytes = Encode(source, new AvifEncoderOptions { Quality = 90, BitDepth = bitDepth, TileRowsLog2 = tileRowsLog2 });
        using var back = Image.Load(bytes);
        Assert.Equal((128, 512), (back.Width, back.Height));
        Assert.True(Psnr(source, back) > 36, $"PSNR {Psnr(source, back):F1} dB");

        // The default picks tile rows by height; a tall frame must work on its own too.
        using var tall = Source(PixelFormat.Rgb48, bitDepth, 96, 576);
        using var tallBack = Image.Load(Encode(tall, new AvifEncoderOptions { Quality = 90, BitDepth = bitDepth }));
        Assert.True(Psnr(tall, tallBack) > 36);
    }

    [Theory]
    [InlineData(PixelFormat.Gray16, 10)]
    [InlineData(PixelFormat.Gray16, 12)]
    [InlineData(PixelFormat.Rgba64, 10)]
    [InlineData(PixelFormat.Rgba64, 12)]
    public void Lossy_GrayAndAlpha_StayClose(PixelFormat format, int bitDepth)
    {
        using var source = Source(format, bitDepth, 64, 64);
        var bytes = Encode(source, new AvifEncoderOptions { Quality = 92, BitDepth = bitDepth });
        using var back = Image.Load(bytes);
        Assert.Equal(format, back.PixelFormat);
        Assert.True(Psnr(source, back) > 34, $"{format} {bitDepth}-bit: PSNR {Psnr(source, back):F1} dB");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void Lossless_EveryEffortRoundTripsExactly(int effort)
    {
        using var source = Source(PixelFormat.Rgba64, 12, 40, 28);
        var bytes = Encode(source, new AvifEncoderOptions { Lossless = true, BitDepth = 12, Effort = effort });
        using var back = Image.Load(bytes);
        Assert.Equal(source.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Fact]
    public void BitDepthDefault_IsTenForSixteenBitSources_AndEightBitStaysEightBit()
    {
        using var wide = Source(PixelFormat.Rgb48, 10);
        var info = Image.Identify(new MemoryStream(Encode(wide, new AvifEncoderOptions { Lossless = true })));
        Assert.Equal(PixelFormat.Rgb48, info.PixelFormat);

        using var narrow = Image.Create(16, 16, PixelFormat.Rgb24);
        Assert.Equal(PixelFormat.Rgb24, Image.Identify(new MemoryStream(Encode(narrow, new AvifEncoderOptions { Lossless = true }))).PixelFormat);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AvifEncoderOptions { BitDepth = 9 });
    }

    [Fact]
    public void HdrSignalling_RoundTripsThroughNclx()
    {
        using var source = Source(PixelFormat.Rgb48, 10, 40, 32);
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.Rec2100Pq);
        var bytes = Encode(source, new AvifEncoderOptions { Lossless = true, BitDepth = 10 });

        Assert.Equal(CicpCode.Of(NamedColorSpace.Rec2100Pq).TransferCharacteristics, Image.Identify(new MemoryStream(bytes)).Cicp?.TransferCharacteristics ?? 0 | 0);
        using var back = Image.Load(bytes);
        Assert.Equal(CicpCode.Of(NamedColorSpace.Rec2100Pq), back.Metadata.Cicp);
        Assert.Equal(source.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Fact]
    public void LinearFloat_EncodesAsTenBitWithProfileAndSignalling()
    {
        using var source = Image.Create(8, 8, PixelFormat.RgbF32);
        MemoryMarshal.Cast<byte, float>(source.GetPixelSpan()).Fill(0.5f);
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearSrgb);
        var bytes = Encode(source, new AvifEncoderOptions { Lossless = true });
        using var back = Image.Load(bytes);
        Assert.Equal(PixelFormat.Rgb48, back.PixelFormat);
        Assert.Equal(NamedColorSpace.LinearSrgb, back.Metadata.GetNamedColorSpace());
    }
}

public class HighBitDepthScreenContentTests
{
    // A synthetic screen: flat panels in a handful of colors, text-like strokes, and a repeated tile (what palette mode and IntraBC exist for).
    internal static Image Screen(int bitDepth, int width = 192, int height = 128)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb48);
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        int max = (1 << bitDepth) - 1;
        int[][] palette = [[0, 0, 0], [max, max, max], [max / 5, max * 4 / 5, max / 3], [max * 9 / 10, max / 7, max / 6], [max / 2, max / 2, max * 7 / 8]];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int tx = x % 48, ty = y % 32; // a 48x32 tile repeated, so IntraBC has exact matches to copy
                int index = ((tx / 12) + (ty / 8)) % palette.Length;
                if ((tx + (ty * 3)) % 17 == 0)
                {
                    index = 1; // a stroke
                }

                for (int c = 0; c < 3; c++)
                {
                    s[(((y * width) + x) * 3) + c] = (ushort)(((palette[index][c] * 65535L) + (max / 2)) / max);
                }
            }
        }

        return image;
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void ScreenContent_RoundTripsExactly_AndCostsAboutAsLittleAsEightBitScreenContent(int bitDepth)
    {
        using var screen = Screen(bitDepth);
        using var ms = new MemoryStream();
        screen.Save(ms, "avif", new AvifEncoderOptions { Lossless = true, BitDepth = bitDepth, Effort = 2 });
        using var back = Image.Load(ms.ToArray());
        Assert.Equal(screen.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());

        // The same picture at 8 bits, which has always used palette and IntraBC: the high-depth stream should be in the same league, not photo-sized.
        using var eight = Screen(8);
        using var ms8 = new MemoryStream();
        using var eightBit = eight.ConvertTo(PixelFormat.Rgb24);
        eightBit.Save(ms8, "avif", new AvifEncoderOptions { Lossless = true, Effort = 2 });
        Assert.True(ms.Length < ms8.Length * 3, $"{bitDepth}-bit screen content took {ms.Length} bytes against {ms8.Length} at 8 bits");
    }
}
