using System.Runtime.InteropServices;
using PeachImage.Formats.Avif;
using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jxl;
using PeachImage.Formats.Png;
using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Color;

public class EncodePrecisionTests
{
    private static byte[] Encode(Image image, string format, EncoderOptions? options = null)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format, options);
        return ms.ToArray();
    }

    private static Image Gradient(PixelFormat format, int width = 16, int height = 4)
    {
        var image = Image.Create(width, height, format);
        using var rgba = Image.Create(width, height, PixelFormat.RgbaF32);
        var f = MemoryMarshal.Cast<byte, float>(rgba.GetPixelSpan());
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * 4;
                f[o] = x / (float)(width - 1);
                f[o + 1] = y / (float)(height - 1);
                f[o + 2] = 0.25f;
                f[o + 3] = 1f;
            }
        }

        using var converted = rgba.ConvertTo(format);
        converted.GetPixelSpan().CopyTo(image.GetPixelSpan());
        return image;
    }

    [Fact]
    public void Png_Rgb48_StaysSixteenBitAndRoundTripsExactly()
    {
        using var source = Gradient(PixelFormat.Rgb48);
        using var back = Image.Load(Encode(source, "png"));
        Assert.Equal(PixelFormat.Rgb48, back.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Png_Rgb24_StaysEightBitByDefault_AndSixteenWhenAsked()
    {
        using var source = Gradient(PixelFormat.Rgb24);
        using var eight = Image.Load(Encode(source, "png"));
        Assert.Equal(PixelFormat.Rgb24, eight.PixelFormat);

        using var sixteen = Image.Load(Encode(source, "png", new PngEncoderOptions { Precision = EncodePrecision.Sixteen }));
        Assert.Equal(PixelFormat.Rgb48, sixteen.PixelFormat);

        using var wide = Image.Load(Encode(source, "png", new PngEncoderOptions { TargetColorSpace = NamedColorSpace.DisplayP3 }));
        Assert.Equal(PixelFormat.Rgb48, wide.PixelFormat);
        Assert.Equal(NamedColorSpace.DisplayP3, wide.Metadata.GetNamedColorSpace());
    }

    [Fact]
    public void Png_LinearFloat_IsWrittenAsSixteenBitWithALinearProfileAndCicp()
    {
        using var linear = Gradient(PixelFormat.RgbaF32);
        linear.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearSrgb);
        linear.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.LinearSrgb).ToArray() });

        using var back = Image.Load(Encode(linear, "png"));
        Assert.Equal(PixelFormat.Rgba64, back.PixelFormat);
        Assert.Equal(NamedColorSpace.LinearSrgb, back.Metadata.GetNamedColorSpace());
        Assert.Equal(CicpCode.Of(NamedColorSpace.LinearSrgb), back.Metadata.Cicp);

        // 16-bit linear keeps the data to within one step.
        var samples = MemoryMarshal.Cast<byte, ushort>(back.GetPixelSpan());
        var original = MemoryMarshal.Cast<byte, float>(linear.GetPixelSpan());
        for (int i = 0; i < original.Length; i++)
        {
            Assert.True(Math.Abs((samples[i] / 65535.0) - original[i]) <= 1.0 / 65535, $"sample {i}");
        }
    }

    [Fact]
    public void Png_Cicp_RoundTripsWithoutAnIccProfile()
    {
        using var source = Gradient(PixelFormat.Rgb24);
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.Srgb);
        using var back = Image.Load(Encode(source, "png"));
        Assert.Equal(CicpCode.Of(NamedColorSpace.Srgb), back.Metadata.Cicp);
        Assert.Null(back.Metadata.IccProfileBytes);

        using var without = Image.Load(Encode(source, "png", new PngEncoderOptions { IncludeMetadata = false }));
        Assert.Null(without.Metadata.Cicp);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    [InlineData("avif")]
    public void TargetColorSpace_EmbedsTheBuiltInProfileInEveryFormat(string format)
    {
        using var source = Gradient(PixelFormat.Rgb24);
        var options = ForFormat(format, NamedColorSpace.DisplayP3);
        using var back = Image.Load(Encode(source, format, options));
        Assert.Equal(NamedColorSpace.DisplayP3, back.Metadata.GetNamedColorSpace());
        Assert.Equal(ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray(), back.Metadata.IccProfileBytes);
    }

    private static EncoderOptions ForFormat(string format, NamedColorSpace space) => format switch
    {
        "png" => new PngEncoderOptions { TargetColorSpace = space },
        "jpeg" => new JpegEncoderOptions { TargetColorSpace = space },
        "webp" => new WebpEncoderOptions { TargetColorSpace = space },
        "avif" => new AvifEncoderOptions { TargetColorSpace = space, Lossless = true },
        _ => new JxlEncoderOptions { TargetColorSpace = space, Lossless = true },
    };

    [Fact]
    public void TargetColorSpace_ConvertsPixelsWhileEncoding()
    {
        using var source = Image.Create(1, 1, PixelFormat.Rgb24);
        source.GetPixelSpan()[0] = 5;
        source.GetPixelSpan()[1] = 250;
        source.GetPixelSpan()[2] = 128;
        using var back = Image.Load(Encode(source, "png", new PngEncoderOptions { TargetColorSpace = NamedColorSpace.DisplayP3 }));
        var samples = MemoryMarshal.Cast<byte, ushort>(back.GetPixelSpan());
        Assert.Equal(0.4504, samples[0] / 65535.0, 3);
        Assert.Equal(0.9660, samples[1] / 65535.0, 3);
        Assert.Equal(0.5524, samples[2] / 65535.0, 3);
    }

    [Fact]
    public void Jpeg_AcceptsSixteenBitFloatAndPremultipliedSources()
    {
        foreach (var format in new[] { PixelFormat.Rgb48, PixelFormat.Rgba64, PixelFormat.RgbaF32, PixelFormat.RgbaF16, PixelFormat.PremultipliedRgba32, PixelFormat.PremultipliedRgbaF32, PixelFormat.Gray16 })
        {
            using var source = Gradient(format);
            using var back = Image.Load(Encode(source, "jpeg", new JpegEncoderOptions { Quality = 95 }));
            Assert.True(back.PixelFormat is PixelFormat.Rgb24 or PixelFormat.Gray8, $"{format} -> {back.PixelFormat}");
            Assert.Equal(source.Width, back.Width);
        }
    }

    [Fact]
    public void Jpeg_PremultipliedSource_IsCompositedOverBlackNotUnpremultiplied()
    {
        using var source = Image.Create(8, 8, PixelFormat.PremultipliedRgba32);
        for (int i = 0; i < 64; i++)
        {
            // straight (200, 200, 200) at alpha 128, stored premultiplied: 100
            source.GetPixelSpan()[(i * 4)..][..4].CopyTo(new byte[4]);
            source.GetPixelSpan()[i * 4] = 100;
            source.GetPixelSpan()[(i * 4) + 1] = 100;
            source.GetPixelSpan()[(i * 4) + 2] = 100;
            source.GetPixelSpan()[(i * 4) + 3] = 128;
        }

        using var back = Image.Load(Encode(source, "jpeg", new JpegEncoderOptions { Quality = 100, Subsampling = JpegChromaSubsampling.Yuv444 }));
        Assert.InRange(back.GetPixelSpan()[0], 97, 103);
    }

    [Fact]
    public void Png_PremultipliedSource_IsUnpremultipliedWhileEncoding()
    {
        using var source = Image.Create(1, 1, PixelFormat.PremultipliedRgba32);
        source.GetPixelSpan()[0] = 100;
        source.GetPixelSpan()[1] = 50;
        source.GetPixelSpan()[2] = 25;
        source.GetPixelSpan()[3] = 128;
        using var back = Image.Load(Encode(source, "png"));
        Assert.Equal(PixelFormat.Rgba32, back.PixelFormat);
        Assert.InRange(back.GetPixelSpan()[0], 198, 200);
        Assert.InRange(back.GetPixelSpan()[1], 99, 100);
        Assert.Equal(128, back.GetPixelSpan()[3]);
    }

    [Fact]
    public void Webp_AndAvif_AcceptFloatAndSixteenBitSources()
    {
        foreach (var format in new[] { PixelFormat.Rgb48, PixelFormat.RgbaF32, PixelFormat.PremultipliedRgba64 })
        {
            using var source = Gradient(format);
            using var webp = Image.Load(Encode(source, "webp", new WebpEncoderOptions { Lossless = true }));
            Assert.True(webp.PixelFormat is PixelFormat.Rgb24 or PixelFormat.Rgba32);
            using var avif = Image.Load(Encode(source, "avif", new AvifEncoderOptions { Lossless = true }));
            Assert.Equal(source.Width, avif.Width);
        }
    }

    [Fact]
    public void LinearFloat_ToAnEightBitFormat_IsStoredSrgbEncoded()
    {
        using var source = Image.Create(1, 1, PixelFormat.RgbF32);
        MemoryMarshal.Cast<byte, float>(source.GetPixelSpan()).Fill(0.5f);
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearSrgb);

        using var back = Image.Load(Encode(source, "webp", new WebpEncoderOptions { Lossless = true }));
        Assert.Equal(188, back.GetPixelSpan()[0]);
        Assert.Equal(NamedColorSpace.Srgb, back.Metadata.GetNamedColorSpace() ?? NamedColorSpace.Srgb);
    }

    [Fact]
    public void QualityFraction_MapsLikeABrowser()
    {
        Assert.Equal(92, new JpegEncoderOptions { QualityFraction = 0.92f }.Quality);
        Assert.Equal(0, new JpegEncoderOptions { QualityFraction = 0f }.Quality);
        Assert.Equal(100, new AvifEncoderOptions { QualityFraction = 1f }.Quality);
        Assert.Equal(51, new JxlEncoderOptions { QualityFraction = 0.505f }.Quality);
        Assert.Equal(0.8f, new WebpEncoderOptions { QualityFraction = 0.8f }.QualityFraction);

        var lossy = new WebpEncoderOptions { QualityFraction = 0.8f };
        Assert.False(lossy.Lossless);
        Assert.True(new WebpEncoderOptions { QualityFraction = 1f }.Lossless);

        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoderOptions { QualityFraction = 1.5f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoderOptions { QualityFraction = float.NaN });
    }

    [Fact]
    public void LargeIccProfile_SpansMultipleJpegSegmentsAndRoundTrips()
    {
        var icc = new byte[150_000];
        new Random(5).NextBytes(icc);
        using var source = Gradient(PixelFormat.Rgb24);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });
        using var back = Image.Load(Encode(source, "jpeg"));
        Assert.Equal(icc, back.Metadata.IccProfileBytes);
    }

    [Fact]
    public void Webp_IccProfile_SurvivesAlongsideAlpha()
    {
        using var source = Gradient(PixelFormat.Rgba32);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });
        source.GetPixelSpan()[3] = 77;
        using var back = Image.Load(Encode(source, "webp", new WebpEncoderOptions { Lossless = true }));
        Assert.Equal(PixelFormat.Rgba32, back.PixelFormat);
        Assert.Equal(77, back.GetPixelSpan()[3]);
        Assert.Equal(NamedColorSpace.DisplayP3, back.Metadata.GetNamedColorSpace());
    }

    [Fact]
    public void EightBitSources_NeedNoPreparation_AndNonManagedCodecsStillRejectFloat()
    {
        using var source = Gradient(PixelFormat.RgbaF32);
        Assert.Throws<NotSupportedException>(() => Encode(source, "bmp"));
        Assert.Throws<NotSupportedException>(() => Encode(source, "gif"));
    }
}

public class IdentifyColorInfoTests
{
    private static byte[] Encode(Image image, string format, EncoderOptions? options = null)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format, options);
        return ms.ToArray();
    }

    private static ImageInfo Identify(byte[] bytes) => Image.Identify(new MemoryStream(bytes));

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void Identify_ReportsTheEmbeddedProfilesColorSpace(string format)
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb24);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });
        var info = Identify(Encode(source, format));
        Assert.Equal(NamedColorSpace.DisplayP3, info.ColorSpace?.Named);
        Assert.Equal(TransferFunction.Srgb, info.ColorSpace?.Transfer);
        Assert.False(info.IsHdr);
        Assert.Equal(8, info.BitsPerSample);
        Assert.False(info.IsHighBitDepth);
    }

    [Fact]
    public void Identify_ReportsCicpHdrAndHighBitDepth()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb48);
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.Rec2100Pq);
        var info = Identify(Encode(source, "png"));
        Assert.Equal(CicpCode.Of(NamedColorSpace.Rec2100Pq), info.Cicp);
        Assert.Equal(TransferFunction.Pq, info.ColorSpace?.Transfer);
        Assert.True(info.IsHdr);
        Assert.Equal(16, info.BitsPerSample);
        Assert.True(info.IsHighBitDepth);
    }

    [Fact]
    public void Identify_ReportsNothingForAnUndeclaredFile_AndHonorsSkipMetadata()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb24);
        Assert.Null(Identify(Encode(source, "png")).ColorSpace);

        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.DisplayP3);
        var bytes = Encode(source, "png");
        Assert.NotNull(Identify(bytes).ColorSpace);
        Assert.Null(Image.Identify(new MemoryStream(bytes), new DecoderOptions { SkipMetadata = true }).ColorSpace);
    }

    [Fact]
    public void Identify_ReadsAMultiSegmentJpegProfile()
    {
        // 150 KB of profile spans three APP2 segments; the scan joins them in order.
        var icc = ColorProfiles.GetBytes(NamedColorSpace.Rec2020).ToArray();
        using var source = Image.Create(4, 4, PixelFormat.Rgb24);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });
        Assert.Equal(NamedColorSpace.Rec2020, Identify(Encode(source, "jpeg")).ColorSpace?.Named);
    }
}
