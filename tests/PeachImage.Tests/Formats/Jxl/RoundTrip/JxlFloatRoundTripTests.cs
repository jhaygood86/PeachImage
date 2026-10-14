using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl;
using PeachImage.Tests.Formats.Jxl.Oracle;

namespace PeachImage.Tests.Formats.Jxl.RoundTrip;

/// <summary>32-bit float images: lossless keeps every bit pattern, lossy stays close, and both decode in libjxl.</summary>
public class JxlFloatRoundTripTests
{
    public static TheoryData<int, int, PixelFormat> Cases => new()
    {
        { 1, 1, PixelFormat.GrayF32 },
        { 17, 9, PixelFormat.GrayF32 },
        { 64, 48, PixelFormat.RgbF32 },
        { 300, 200, PixelFormat.RgbF32 },
        { 40, 30, PixelFormat.RgbaF32 },
        { 520, 270, PixelFormat.RgbaF32 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Lossless_PreservesEveryBitPattern(int width, int height, PixelFormat format)
    {
        var source = Create(width, height, format, hdr: true);

        using var stream = new MemoryStream(Encode(source, new JxlEncoderOptions { Lossless = true }));
        var decoded = Image.Load(stream);

        Assert.Equal(format, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()), "Decoded float bits differ from the source.");
    }

    [Fact]
    public void Lossless_KeepsSpecialValues()
    {
        var source = Image.Create(8, 1, PixelFormat.GrayF32);
        var samples = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        float[] values = [0f, -0f, 1f, -1f, float.Epsilon, 3.5e30f, float.PositiveInfinity, float.NaN];
        values.CopyTo(samples);

        using var stream = new MemoryStream(Encode(source, new JxlEncoderOptions { Lossless = true }));
        var decoded = Image.Load(stream);

        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void OpaqueRgbaF32_IsStoredWithoutAlpha()
    {
        var source = Create(20, 10, PixelFormat.RgbaF32, hdr: false);
        var samples = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        for (int i = 3; i < samples.Length; i += 4)
        {
            samples[i] = 1f;
        }

        using var stream = new MemoryStream(Encode(source, new JxlEncoderOptions { Lossless = true }));
        Assert.False(Image.Identify(stream).HasAlpha);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Lossy_StaysCloseToTheSource(int width, int height, PixelFormat format)
    {
        var source = Create(width, height, format, hdr: false);

        using var stream = new MemoryStream(Encode(source, new JxlEncoderOptions { Quality = 90 }));
        var decoded = Image.Load(stream);

        Assert.Equal(format, decoded.PixelFormat);
        var a = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        var b = MemoryMarshal.Cast<byte, float>(decoded.GetPixelSpan());
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }

        double psnr = 10 * Math.Log10(1.0 / ((sum / a.Length) + 1e-12));
        Assert.True(psnr >= 26, $"PSNR {psnr:F1} dB is too low.");
    }

    [Fact]
    public void Lossy_KeepsHdrValuesAboveOne()
    {
        var source = Image.Create(64, 64, PixelFormat.RgbF32);
        var samples = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        samples.Fill(4f);

        using var stream = new MemoryStream(Encode(source, new JxlEncoderOptions { Quality = 95 }));
        var decoded = MemoryMarshal.Cast<byte, float>(Image.Load(stream).GetPixelSpan());

        Assert.InRange(decoded[decoded.Length / 2], 3.5f, 4.5f);
    }

    [Theory]
    [InlineData(64, 48, PixelFormat.RgbF32, "rgbf32le")]
    [InlineData(37, 29, PixelFormat.GrayF32, "grayf32le")]
    [InlineData(300, 200, PixelFormat.RgbaF32, "rgbaf32le")]
    public void LosslessFloat_DecodesToTheSourceWithLibjxl(int width, int height, PixelFormat format, string referenceFormat)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        // libjxl exposes float output in [0, 1] sRGB-style values; keep the source in that range so any scaling would show.
        var source = Create(width, height, format, hdr: false);
        byte[] jxl = Encode(source, new JxlEncoderOptions { Lossless = true });

        byte[] reference = LibjxlOracle.Decode(jxl, referenceFormat);

        Assert.Equal(source.GetPixelSpan().Length, reference.Length);
        var a = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        var b = MemoryMarshal.Cast<byte, float>(reference.AsSpan());
        for (int i = 0; i < a.Length; i++)
        {
            Assert.True(MathF.Abs(a[i] - b[i]) <= 1e-6f, $"Sample {i}: wrote {a[i]}, libjxl decoded {b[i]}.");
        }
    }

    private static byte[] Encode(Image image, JxlEncoderOptions options)
    {
        using var output = new MemoryStream();
        image.Save(output, "jxl", options);
        return output.ToArray();
    }

    private static Image Create(int width, int height, PixelFormat format, bool hdr)
    {
        var image = Image.Create(width, height, format);
        var samples = MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        var random = new Random((width * 17) + height);
        int channels = format == PixelFormat.GrayF32 ? 1 : format == PixelFormat.RgbF32 ? 3 : 4;
        for (int i = 0; i < samples.Length; i++)
        {
            int pixel = i / channels;
            int c = i % channels;
            double u = (pixel % width) / (double)Math.Max(1, width - 1);
            double v = (pixel / width) / (double)Math.Max(1, height - 1);
            double value = 0.5 + (0.3 * Math.Sin((u * 5) + c)) + (0.15 * Math.Cos((v * 4) - c)) + ((random.NextDouble() - 0.5) * 0.02);
            if (c == 3)
            {
                value = u;
            }

            samples[i] = hdr ? (float)((value * 3) - 0.5) : (float)Math.Clamp(value, 0, 1);
        }

        return image;
    }
}
