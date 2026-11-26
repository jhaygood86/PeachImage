using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl;

namespace PeachImage.Tests.Formats.Jxl.RoundTrip;

public class JxlColorSignallingTests
{
    private static byte[] Encode(Image image, JxlEncoderOptions options)
    {
        using var ms = new MemoryStream();
        image.Save(ms, "jxl", options);
        return ms.ToArray();
    }

    private static Image Ramp(PixelFormat format, int width = 24, int height = 16)
    {
        var image = Image.Create(width, height, format);
        int channels = format.GetChannelCount();
        switch (format)
        {
            case PixelFormat.RgbF32:
            case PixelFormat.RgbaF32:
            {
                var f = MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
                for (int i = 0; i < width * height; i++)
                {
                    f[i * channels] = i * 3f / (width * height);
                    f[(i * channels) + 1] = (i % width) / (float)width;
                    f[(i * channels) + 2] = 0.1f + (i / (float)(width * height) * 0.5f);
                    if (channels == 4)
                    {
                        f[(i * channels) + 3] = 0.25f + ((i % 7) * 0.1f);
                    }
                }

                break;
            }

            case PixelFormat.RgbaF16:
            {
                var h = MemoryMarshal.Cast<byte, Half>(image.GetPixelSpan());
                for (int i = 0; i < width * height; i++)
                {
                    h[i * 4] = (Half)(i * 6f / (width * height)); // up to 6: HDR headroom
                    h[(i * 4) + 1] = (Half)((i % width) / (float)width);
                    h[(i * 4) + 2] = (Half)(0.001f * (i % 50));
                    h[(i * 4) + 3] = (Half)(0.2f + ((i % 9) * 0.1f));
                }

                break;
            }

            default:
            {
                var s = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
                for (int i = 0; i < s.Length; i++)
                {
                    s[i] = (ushort)((i * 977) & 0xFFFF);
                }

                break;
            }
        }

        return image;
    }

    [Fact]
    public void HalfFloat_RoundTripsLosslessly_WithAlphaAndHeadroom()
    {
        using var source = Ramp(PixelFormat.RgbaF16);
        var bytes = Encode(source, new JxlEncoderOptions { Lossless = true });
        using var back = Image.Load(bytes);

        // The file declares 16-bit floats; it decodes to float samples whose values are exactly the halves.
        Assert.True(back.PixelFormat.IsFloat());
        using var narrowed = back.ConvertTo(PixelFormat.RgbaF16);
        Assert.Equal(source.GetPixelSpan().ToArray(), narrowed.GetPixelSpan().ToArray());
        Assert.Contains(MemoryMarshal.Cast<byte, Half>(narrowed.GetPixelSpan()).ToArray(), v => (float)v > 5f);
    }

    [Fact]
    public void HalfFloat_IsSmallerThanWideningToThirtyTwoBits()
    {
        using var half = Ramp(PixelFormat.RgbaF16, 64, 64);
        using var wide = half.ConvertTo(PixelFormat.RgbaF32);
        Assert.True(Encode(half, new JxlEncoderOptions { Lossless = true }).Length < Encode(wide, new JxlEncoderOptions { Lossless = true }).Length);
    }

    [Theory]
    [InlineData(NamedColorSpace.DisplayP3)]
    [InlineData(NamedColorSpace.Rec2020)]
    [InlineData(NamedColorSpace.LinearSrgb)]
    [InlineData(NamedColorSpace.LinearDisplayP3)]
    [InlineData(NamedColorSpace.Rec2100Pq)]
    [InlineData(NamedColorSpace.Rec2100Hlg)]
    public void Lossless_DeclaresTheSpaceWithEnums_AndKeepsEverySample(NamedColorSpace space)
    {
        using var source = Ramp(PixelFormat.Rgb48);
        source.Metadata.Cicp = CicpCode.Of(space);
        var bytes = Encode(source, new JxlEncoderOptions { Lossless = true });

        var info = Image.Identify(new MemoryStream(bytes));
        Assert.Equal(space, info.Cicp?.ToNamedColorSpace());
        Assert.Equal(space is NamedColorSpace.Rec2100Pq or NamedColorSpace.Rec2100Hlg, info.IsHdr);

        using var back = Image.Load(bytes);
        Assert.Equal(space, back.Metadata.Cicp?.ToNamedColorSpace());
        Assert.Null(back.Metadata.IccProfileBytes); // enums, not a profile
        Assert.Equal(source.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Lossless_BuiltInIccProfile_BecomesEnumsAndComesBackAsCicp()
    {
        using var source = Ramp(PixelFormat.Rgb48);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });
        var bytes = Encode(source, new JxlEncoderOptions { Lossless = true });
        using var back = Image.Load(bytes);
        Assert.Equal(NamedColorSpace.DisplayP3, back.Metadata.GetNamedColorSpace());
    }

    [Fact]
    public void Lossless_UnrecognizedIccProfile_IsStillEmbedded()
    {
        var icc = new byte[400];
        new Random(2).NextBytes(icc);
        using var source = Ramp(PixelFormat.Rgb48);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });
        using var back = Image.Load(Encode(source, new JxlEncoderOptions { Lossless = true }));
        Assert.Equal(icc, back.Metadata.IccProfileBytes);
    }

    [Fact]
    public void LinearFloat_Lossless_KeepsBitsAndDeclaresLinear()
    {
        using var source = Ramp(PixelFormat.RgbF32);
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearSrgb);
        using var back = Image.Load(Encode(source, new JxlEncoderOptions { Lossless = true }));
        Assert.Equal(PixelFormat.RgbF32, back.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
        Assert.Equal(NamedColorSpace.LinearSrgb, back.Metadata.GetNamedColorSpace());
    }

    [Fact]
    public void LinearFloat_Lossy_DecodesBackToLinearLight()
    {
        using var source = Image.Create(32, 32, PixelFormat.RgbF32);
        var f = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = 0.05f + (((i / 3) % 32) / 32f * 0.9f);
        }

        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearSrgb);
        var bytes = Encode(source, new JxlEncoderOptions { Quality = 95 });
        using var back = Image.Load(bytes);
        Assert.Equal(NamedColorSpace.LinearSrgb, back.Metadata.GetNamedColorSpace());
        var g = MemoryMarshal.Cast<byte, float>(back.GetPixelSpan());
        double worst = 0;
        for (int i = 0; i < f.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(f[i] - g[i]));
        }

        Assert.True(worst < 0.05, $"worst linear-light error {worst}");
    }

    private static Image SaturatedScene(NamedColorSpace space, PixelFormat format, int width = 48, int height = 32)
    {
        // Saturated primaries and secondaries with soft ramps: many of these lie outside the sRGB gamut for P3 and Rec. 2020.
        var image = Image.Create(width, height, PixelFormat.RgbF32);
        var f = MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * 3;
                float t = x / (float)(width - 1);
                float u = y / (float)(height - 1);
                f[o] = 0.04f + (0.2f * t * u);
                f[o + 1] = 0.35f + (0.6f * (1 - t) * (1 - (0.4f * u)));
                f[o + 2] = 0.05f + (0.3f * u);
            }
        }

        image.Metadata.Cicp = CicpCode.Of(space);
        var converted = image.ConvertTo(format);
        if (!ReferenceEquals(image, converted))
        {
            image.Dispose();
        }

        return converted;
    }

    private static double Psnr(Image a, Image b)
    {
        var x = a.ConvertTo(PixelFormat.RgbF32);
        var y = b.ConvertTo(PixelFormat.RgbF32);
        var p = MemoryMarshal.Cast<byte, float>(x.GetPixelSpan());
        var q = MemoryMarshal.Cast<byte, float>(y.GetPixelSpan());
        double sum = 0;
        for (int i = 0; i < p.Length; i++)
        {
            double d = p[i] - q[i];
            sum += d * d;
        }

        if (!ReferenceEquals(x, a))
        {
            x.Dispose();
        }

        if (!ReferenceEquals(y, b))
        {
            y.Dispose();
        }

        return 10 * Math.Log10(1.0 / (sum / p.Length));
    }

    [Theory]
    [InlineData(NamedColorSpace.DisplayP3, PixelFormat.Rgb48)]
    [InlineData(NamedColorSpace.DisplayP3, PixelFormat.Rgb24)]
    [InlineData(NamedColorSpace.Rec2020, PixelFormat.Rgb48)]
    [InlineData(NamedColorSpace.LinearDisplayP3, PixelFormat.RgbF32)]
    [InlineData(NamedColorSpace.LinearRec2020, PixelFormat.RgbF32)]
    public void Lossy_WideGamut_KeepsTheDeclaredSpaceAndTheColorsOutsideSrgb(NamedColorSpace space, PixelFormat format)
    {
        using var source = SaturatedScene(space, format);
        var bytes = Encode(source, new JxlEncoderOptions { Quality = 95 });
        using var back = Image.Load(bytes);
        Assert.Equal(space, back.Metadata.GetNamedColorSpace());
        Assert.Equal(format, back.PixelFormat);
        Assert.True(Psnr(source, back) > 32, $"PSNR {Psnr(source, back):F1} dB");

        // The point of the exercise: the colors outside sRGB are still there. Converting both to sRGB with Preserve shows negative components in both.
        var preserve = new ColorConversionOptions { GamutHandling = GamutHandling.Preserve, TargetPixelFormat = PixelFormat.RgbF32 };
        using var a = source.ConvertColorSpace(NamedColorSpace.LinearSrgb, preserve);
        using var b = back.ConvertColorSpace(NamedColorSpace.LinearSrgb, preserve);
        var fa = MemoryMarshal.Cast<byte, float>(a.GetPixelSpan());
        var fb = MemoryMarshal.Cast<byte, float>(b.GetPixelSpan());
        float minA = fa.ToArray().Min();
        float minB = fb.ToArray().Min();
        Assert.True(minA < -0.02f, $"the scene should lie outside sRGB (min {minA})");
        Assert.True(Math.Abs(minA - minB) < 0.03f, $"out-of-gamut colors changed: {minA} vs {minB}");
    }

    [Theory]
    [InlineData(NamedColorSpace.Rec2100Pq)]
    [InlineData(NamedColorSpace.Rec2100Hlg)]
    public void Lossy_Hdr_KeepsTheHeadroom(NamedColorSpace space)
    {
        // A scene whose highlights are several times SDR white, as PQ/HLG signal values.
        using var linear = Image.Create(40, 24, PixelFormat.RgbF32);
        var f = MemoryMarshal.Cast<byte, float>(linear.GetPixelSpan());
        for (int i = 0; i < f.Length; i += 3)
        {
            float t = (i / 3 % 40) / 39f;
            f[i] = 0.05f + (3.2f * t * t);
            f[i + 1] = 0.05f + (2.4f * t);
            f[i + 2] = 0.05f + (0.8f * t);
        }

        linear.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearRec2020);
        using var signal = linear.ConvertColorSpace(space, new ColorConversionOptions { TargetPixelFormat = PixelFormat.RgbF32 });
        using var source = signal.ConvertTo(PixelFormat.Rgb48);
        source.Metadata.Cicp = CicpCode.Of(space);

        var bytes = Encode(source, new JxlEncoderOptions { Quality = 96 });
        var info = Image.Identify(new MemoryStream(bytes));
        Assert.True(info.IsHdr);

        using var back = Image.Load(bytes);
        Assert.Equal(space, back.Metadata.Cicp?.ToNamedColorSpace());
        var keep = new ColorConversionOptions { GamutHandling = GamutHandling.Preserve, TargetPixelFormat = PixelFormat.RgbF32 };
        using var before = source.ConvertColorSpace(NamedColorSpace.LinearRec2020, keep);
        using var after = back.ConvertColorSpace(NamedColorSpace.LinearRec2020, keep);
        var fb = MemoryMarshal.Cast<byte, float>(before.GetPixelSpan());
        var fa = MemoryMarshal.Cast<byte, float>(after.GetPixelSpan());
        Assert.True(fb.ToArray().Max() > 2.5f);
        for (int i = 0; i < fb.Length; i++)
        {
            Assert.True(Math.Abs(fb[i] - fa[i]) <= 0.08f + (0.06f * fb[i]), $"sample {i}: {fb[i]} became {fa[i]}");
        }
    }

    [Theory]
    [InlineData(NamedColorSpace.Rec2100Pq)]
    [InlineData(NamedColorSpace.Rec2100Hlg)]
    [InlineData(NamedColorSpace.LinearSrgb)]
    [InlineData(NamedColorSpace.Rec2020)]
    public void Gray_Lossless_DeclaresItsTransferFunction(NamedColorSpace space)
    {
        using var source = Image.Create(24, 16, PixelFormat.Gray16);
        var s = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (ushort)((i * 811) & 0xFFFF);
        }

        source.Metadata.Cicp = CicpCode.Of(space);
        var bytes = Encode(source, new JxlEncoderOptions { Lossless = true });
        var info = Image.Identify(new MemoryStream(bytes));
        Assert.Equal(CicpCode.Of(space).Transfer, info.Cicp?.Transfer);
        Assert.Equal(space is NamedColorSpace.Rec2100Pq or NamedColorSpace.Rec2100Hlg, info.IsHdr);

        using var back = Image.Load(bytes);
        Assert.Equal(PixelFormat.Gray16, back.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
        Assert.Equal(CicpCode.Of(space).Transfer, back.Metadata.Cicp?.Transfer);
    }

    [Fact]
    public void Gray_Lossy_KeepsLinearLightAndHdrHeadroom()
    {
        using var linear = Image.Create(32, 32, PixelFormat.GrayF32);
        var f = MemoryMarshal.Cast<byte, float>(linear.GetPixelSpan());
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = 0.05f + ((i % 32) / 31f * 3.4f); // up to 3.45 times SDR white
        }

        // Linear sRGB gray: decodes back as linear light.
        linear.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearSrgb);
        using var lin = Image.Load(Encode(linear, new JxlEncoderOptions { Quality = 96 }));
        Assert.Equal(TransferFunction.Linear, lin.Metadata.Cicp?.Transfer);
        var l = MemoryMarshal.Cast<byte, float>(lin.GetPixelSpan());
        for (int i = 0; i < f.Length; i++)
        {
            Assert.True(Math.Abs(f[i] - l[i]) <= 0.06f + (0.04f * f[i]), $"linear sample {i}: {f[i]} became {l[i]}");
        }

        // The same light as a PQ gray image: the signal decodes back to nearly the same light.
        using var pq = Image.Create(32, 32, PixelFormat.GrayF32);
        var p = MemoryMarshal.Cast<byte, float>(pq.GetPixelSpan());
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = (float)PeachImage.Internal.Color.Hdr.LinearToPq(f[i]);
        }

        pq.Metadata.Cicp = CicpCode.Of(NamedColorSpace.Rec2100Pq);
        var bytes = Encode(pq, new JxlEncoderOptions { Quality = 96 });
        Assert.True(Image.Identify(new MemoryStream(bytes)).IsHdr);
        using var back = Image.Load(bytes);
        var b = MemoryMarshal.Cast<byte, float>(back.GetPixelSpan());
        for (int i = 0; i < p.Length; i++)
        {
            double before = PeachImage.Internal.Color.Hdr.PqToLinear(p[i]);
            double after = PeachImage.Internal.Color.Hdr.PqToLinear(b[i]);
            Assert.True(Math.Abs(before - after) <= 0.1 + (0.06 * before), $"PQ sample {i}: {before} became {after}");
        }
    }

    [Fact]
    public void HalfFloat_CanBeDecodedDirectlyToRgbaF16_AndToPremultipliedFormats()
    {
        using var source = Ramp(PixelFormat.RgbaF16);
        var bytes = Encode(source, new JxlEncoderOptions { Lossless = true });

        using var half = Image.Load(new MemoryStream(bytes), new DecoderOptions { TargetPixelFormat = PixelFormat.RgbaF16 });
        Assert.Equal(PixelFormat.RgbaF16, half.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), half.GetPixelSpan().ToArray());

        using var premultiplied = Image.Load(new MemoryStream(bytes), new DecoderOptions { TargetPixelFormat = PixelFormat.PremultipliedRgbaF16 });
        Assert.Equal(PixelFormat.PremultipliedRgbaF16, premultiplied.PixelFormat);
        using var straight = premultiplied.ConvertTo(PixelFormat.RgbaF16);
        var a = MemoryMarshal.Cast<byte, Half>(source.GetPixelSpan());
        var b = MemoryMarshal.Cast<byte, Half>(straight.GetPixelSpan());
        for (int i = 0; i < a.Length; i++)
        {
            Assert.True(Math.Abs((float)a[i] - (float)b[i]) <= 0.01f + (0.002f * Math.Abs((float)a[i])), $"sample {i}");
        }

        // Other formats take the new targets too: an 8-bit PNG straight to premultiplied RGBA.
        using var png = Image.Create(4, 4, PixelFormat.Rgba32);
        png.GetPixelSpan().Fill(200);
        using var ms = new MemoryStream();
        png.Save(ms, "png");
        using var premultipliedPng = Image.Load(new MemoryStream(ms.ToArray()), new DecoderOptions { TargetPixelFormat = PixelFormat.PremultipliedRgba32 });
        Assert.Equal(PixelFormat.PremultipliedRgba32, premultipliedPng.PixelFormat);
        Assert.InRange(premultipliedPng.GetPixelSpan()[0], 156, 158); // 200 * 200/255
    }

    [Fact]
    public void Lossy_UnrecognizedProfile_IsStillConvertedToSrgb()
    {
        using var source = Ramp(PixelFormat.Rgb48, 32, 32);
        var icc = new byte[400];
        new Random(3).NextBytes(icc);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });
        using var back = Image.Load(Encode(source, new JxlEncoderOptions { Quality = 90 }));
        Assert.Null(back.Metadata.IccProfileBytes);
    }


    [Fact]
    public void Premultiplied_AndSixteenBitSources_EncodeLosslessly()
    {
        using var source = Ramp(PixelFormat.PremultipliedRgbaF32.ToStraightAlpha() == PixelFormat.RgbaF32 ? PixelFormat.RgbaF32 : PixelFormat.RgbF32);
        using var premultiplied = source.ConvertTo(PixelFormat.PremultipliedRgbaF16);
        using var back = Image.Load(Encode(premultiplied, new JxlEncoderOptions { Lossless = true }));
        Assert.True(back.PixelFormat.IsFloat());
        Assert.True(back.HasAlpha || back.PixelFormat.HasAlpha());
    }
}
