using System.Runtime.InteropServices;
using PeachImage.Formats.Png;
using JpegConverter = PeachImage.Formats.Jpeg.Decoding.PixelFormatConverter;
using PngConverter = PeachImage.Formats.Png.Decoding.PixelFormatConverter;
using AvifConverter = PeachImage.Formats.Avif.Decoding.PixelFormatConverter;
using WebpConverter = PeachImage.Formats.Webp.Decoding.PixelFormatConverter;
using BmpConverter = PeachImage.Formats.Bmp.Decoding.PixelFormatConverter;
using GifConverter = PeachImage.Formats.Gif.Decoding.PixelFormatConverter;

namespace PeachImage.Tests.PixelFormatConversion;

/// <summary>
/// Pins the contract a "always request Rgba32, then treat any exception as a decode failure" caller (e.g.
/// PeachPDF, which funnels every decoded image into a single Rgba32-only re-encode pipeline) relies on:
/// requesting <see cref="DecoderOptions.TargetPixelFormat"/> = <see cref="PixelFormat.Rgba32"/> must succeed,
/// in one conversion hop, for every native <see cref="PixelFormat"/> a decoder can actually produce. Without
/// this, "conversion path not implemented" and "file is corrupt" throw the same exception type, so a caller
/// can't safely fall back. If a decoder's pixel-format selector (e.g. <c>AvifPixelFormatSelector</c>,
/// <c>PngPixelFormatSelector</c>) gains a new native format, add it to that format's list below too.
/// </summary>
public class Rgba32ConversionCompletenessTests
{
    public static TheoryData<PixelFormat> JpegNativeFormats() =>
        [PixelFormat.Gray8, PixelFormat.Rgb24, PixelFormat.Cmyk32];

    public static TheoryData<PixelFormat> PngNativeFormats() =>
        [PixelFormat.Gray8, PixelFormat.Rgb24, PixelFormat.Gray16, PixelFormat.Rgb48, PixelFormat.Rgba64];

    public static TheoryData<PixelFormat> AvifNativeFormats() =>
        [PixelFormat.Gray8, PixelFormat.Rgb24, PixelFormat.Gray16, PixelFormat.Rgb48, PixelFormat.Rgba64];

    public static TheoryData<PixelFormat> WebpNativeFormats() => [PixelFormat.Rgb24];

    public static TheoryData<PixelFormat> BmpNativeFormats() => [PixelFormat.Rgb24];

    public static TheoryData<PixelFormat> GifNativeFormats() => [PixelFormat.Rgb24];

    [Theory]
    [MemberData(nameof(JpegNativeFormats))]
    public void Jpeg_EveryNativeFormat_ConvertsToRgba32InOneHop(PixelFormat source) =>
        AssertConvertsToRgba32(source, JpegConverter.ConvertIfNeeded);

    [Theory]
    [MemberData(nameof(PngNativeFormats))]
    public void Png_EveryNativeFormat_ConvertsToRgba32InOneHop(PixelFormat source) =>
        AssertConvertsToRgba32(source, PngConverter.ConvertIfNeeded);

    [Theory]
    [MemberData(nameof(AvifNativeFormats))]
    public void Avif_EveryNativeFormat_ConvertsToRgba32InOneHop(PixelFormat source) =>
        AssertConvertsToRgba32(source, AvifConverter.ConvertIfNeeded);

    [Theory]
    [MemberData(nameof(WebpNativeFormats))]
    public void Webp_EveryNativeFormat_ConvertsToRgba32InOneHop(PixelFormat source) =>
        AssertConvertsToRgba32(source, WebpConverter.ConvertIfNeeded);

    [Theory]
    [MemberData(nameof(BmpNativeFormats))]
    public void Bmp_EveryNativeFormat_ConvertsToRgba32InOneHop(PixelFormat source) =>
        AssertConvertsToRgba32(source, BmpConverter.ConvertIfNeeded);

    [Theory]
    [MemberData(nameof(GifNativeFormats))]
    public void Gif_EveryNativeFormat_ConvertsToRgba32InOneHop(PixelFormat source) =>
        AssertConvertsToRgba32(source, GifConverter.ConvertIfNeeded);

    private static void AssertConvertsToRgba32(PixelFormat source, Func<Image, PixelFormat?, Image> convert)
    {
        using var image = CreateFilledImage(3, 2, source);
        using var converted = convert(image, PixelFormat.Rgba32);

        Assert.Equal(PixelFormat.Rgba32, converted.PixelFormat);
        Assert.Equal(image.Width, converted.Width);
        Assert.Equal(image.Height, converted.Height);
        Assert.Equal(image.Width * image.Height * 4, converted.GetPixelSpan().Length);
    }

    [Fact]
    public void Jpeg_Cmyk32_ConvertsToRgba32_UsingNaiveAdditiveFormula()
    {
        using var source = Image.Create(1, 1, PixelFormat.Cmyk32);
        var pixels = source.GetPixelSpan();
        pixels[0] = 10; // C
        pixels[1] = 20; // M
        pixels[2] = 30; // Y
        pixels[3] = 40; // K

        using var converted = JpegConverter.ConvertIfNeeded(source, PixelFormat.Rgba32);

        var rgba = converted.GetPixelSpan();
        Assert.Equal(205, rgba[0]); // 255 - min(255, C + K) = 255 - 50
        Assert.Equal(195, rgba[1]); // 255 - min(255, M + K) = 255 - 60
        Assert.Equal(185, rgba[2]); // 255 - min(255, Y + K) = 255 - 70
        Assert.Equal(255, rgba[3]);
    }

    [Fact]
    public void Jpeg_Cmyk32_ConvertsToRgba32_ClampsInsteadOfWrapping()
    {
        using var source = Image.Create(1, 1, PixelFormat.Cmyk32);
        var pixels = source.GetPixelSpan();
        pixels[0] = 255; // C
        pixels[1] = 0;   // M
        pixels[2] = 0;   // Y
        pixels[3] = 255; // K

        using var converted = JpegConverter.ConvertIfNeeded(source, PixelFormat.Rgba32);

        var rgba = converted.GetPixelSpan();
        Assert.Equal(0, rgba[0]); // 255 - min(255, 255 + 255) = 0, not an underflow wrap to a large byte value.
        Assert.Equal(0, rgba[1]); // 255 - min(255, 0 + 255) = 0
        Assert.Equal(0, rgba[2]); // 255 - min(255, 0 + 255) = 0
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Png_Gray16_ConvertsToRgba32_ThroughImageLoad_TopByteTruncated(bool interlace)
    {
        using var source = Image.Create(2, 2, PixelFormat.Gray16);
        MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan()).Fill(0xABCD);

        using var decoded = EncodePngThenLoadAsRgba32(source, interlace);

        var rgba = decoded.GetPixelSpan();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.Equal(0xAB, rgba[i]);
            Assert.Equal(0xAB, rgba[i + 1]);
            Assert.Equal(0xAB, rgba[i + 2]);
            Assert.Equal(255, rgba[i + 3]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Png_Rgb48_ConvertsToRgba32_ThroughImageLoad_TopByteTruncated(bool interlace)
    {
        using var source = Image.Create(2, 2, PixelFormat.Rgb48);
        var samples = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        for (int i = 0; i < samples.Length; i += 3)
        {
            samples[i] = 0x0100;     // R -> 1
            samples[i + 1] = 0x8000; // G -> 128
            samples[i + 2] = 0xFF00; // B -> 255
        }

        using var decoded = EncodePngThenLoadAsRgba32(source, interlace);

        var rgba = decoded.GetPixelSpan();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.Equal(1, rgba[i]);
            Assert.Equal(128, rgba[i + 1]);
            Assert.Equal(255, rgba[i + 2]);
            Assert.Equal(255, rgba[i + 3]);
        }
    }

    [Fact]
    public void Avif_Rgba64_ConvertsToRgba32_TopByteTruncated_IncludingAlpha()
    {
        using var source = Image.Create(1, 1, PixelFormat.Rgba64);
        var samples = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        samples[0] = 0x0100; // R -> 1
        samples[1] = 0x8000; // G -> 128
        samples[2] = 0xFF00; // B -> 255
        samples[3] = 0x4000; // A -> 64

        using var converted = AvifConverter.ConvertIfNeeded(source, PixelFormat.Rgba32);

        var rgba = converted.GetPixelSpan();
        Assert.Equal(1, rgba[0]);
        Assert.Equal(128, rgba[1]);
        Assert.Equal(255, rgba[2]);
        Assert.Equal(64, rgba[3]);
    }

    private static Image EncodePngThenLoadAsRgba32(Image source, bool interlace)
    {
        using var stream = new MemoryStream();
        PngEncoder.Encode(source, stream, new PngEncoderOptions { Interlace = interlace });
        stream.Position = 0;

        var decoded = Image.Load(stream, new PngDecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        return decoded;
    }

    private static Image CreateFilledImage(int width, int height, PixelFormat format)
    {
        var image = Image.Create(width, height, format);
        if (format.GetBytesPerSample() == 1)
        {
            var pixels = image.GetPixelSpan();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)((i * 37) + 11);
            }
        }
        else
        {
            var samples = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (ushort)((i * 4111) + 257);
            }
        }

        return image;
    }
}
