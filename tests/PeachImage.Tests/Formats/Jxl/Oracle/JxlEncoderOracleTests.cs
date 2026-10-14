using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Encodes with PeachImage and decodes with libjxl (through ffmpeg), the reference implementation, so the encoder's
/// output is checked by a decoder that shares no code with this repository.
/// </summary>
[Trait("Category", "Oracle")]
public class JxlEncoderOracleTests
{
    // (width, height, format, libjxl output pix_fmt)
    public static TheoryData<int, int, PixelFormat, string> Cases() => new()
    {
        { 64, 48, PixelFormat.Rgb24, "rgb24" },
        { 1, 1, PixelFormat.Rgb24, "rgb24" },
        { 37, 29, PixelFormat.Gray8, "gray" },
        { 300, 200, PixelFormat.Rgba32, "rgba" },
        { 600, 530, PixelFormat.Rgb24, "rgb24" },
        { 257, 513, PixelFormat.Gray8, "gray" },
        { 90, 70, PixelFormat.Rgb48, "rgb48le" },
        { 400, 300, PixelFormat.Rgba64, "rgba64le" },
        { 123, 45, PixelFormat.Gray16, "gray16le" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void LosslessOutput_DecodesToTheSourceWithLibjxl(int width, int height, PixelFormat format, string referenceFormat)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        var source = CreateImage(width, height, format);
        using var output = new MemoryStream();
        source.Save(output, "jxl", new JxlEncoderOptions { Lossless = true });

        byte[] reference = LibjxlOracle.Decode(output.ToArray(), referenceFormat);

        Assert.Equal(source.GetPixelSpan().Length, reference.Length);
        Assert.True(source.GetPixelSpan().SequenceEqual(reference), "libjxl decoded different pixels than were encoded.");
    }

    private static Image CreateImage(int width, int height, PixelFormat format)
    {
        var image = Image.Create(width, height, format);
        var random = new Random((width * 131) + height);
        var pixels = image.GetPixelSpan();
        if (format is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64)
        {
            var samples = MemoryMarshal.Cast<byte, ushort>(pixels);
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (ushort)((i * 37) + random.Next(2048));
            }

            return image;
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i / 3 % width * 2) + (i / 3 / width) + random.Next(40));
        }

        return image;
    }

    // (width, height, format, libjxl output pix_fmt, our expected format)
    public static TheoryData<int, int, PixelFormat, string> LossyCases() => new()
    {
        { 64, 48, PixelFormat.Rgb24, "rgb24" },
        { 1, 1, PixelFormat.Rgb24, "rgb24" },
        { 37, 29, PixelFormat.Gray8, "gray" },
        { 300, 200, PixelFormat.Rgba32, "rgba" },
        { 600, 530, PixelFormat.Rgb24, "rgb24" },
        { 257, 513, PixelFormat.Gray8, "gray" },
        { 90, 70, PixelFormat.Rgb48, "rgb48le" },
        { 123, 45, PixelFormat.Gray16, "gray16le" },
    };

    [Theory]
    [MemberData(nameof(LossyCases))]
    public void LossyOutput_DecodesLikeThisDecoderWithLibjxl(int width, int height, PixelFormat format, string referenceFormat)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        var source = RoundTrip.JxlLossyRoundTripTests.Create(width, height, format);
        using var output = new MemoryStream();
        source.Save(output, "jxl", new JxlEncoderOptions { Quality = 85 });
        byte[] jxl = output.ToArray();

        byte[] reference = LibjxlOracle.Decode(jxl, referenceFormat);
        using var stream = new MemoryStream(jxl);
        var ours = Image.Load(stream);

        Assert.Equal(ours.GetPixelSpan().Length, reference.Length);

        // Both decoders implement the same format; they may differ only by rounding in the float pipeline.
        bool sixteen = format is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
        int tolerance = sixteen ? 64 : 2;
        var a = sixteen ? MemoryMarshal.Cast<byte, ushort>(ours.GetPixelSpan()).ToArray().Select(v => (int)v).ToArray() : ours.GetPixelSpan().ToArray().Select(v => (int)v).ToArray();
        var b = sixteen ? MemoryMarshal.Cast<byte, ushort>(reference.AsSpan()).ToArray().Select(v => (int)v).ToArray() : reference.Select(v => (int)v).ToArray();
        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        }

        Assert.True(worst <= tolerance, $"Decoders disagree by up to {worst}.");
    }
}
