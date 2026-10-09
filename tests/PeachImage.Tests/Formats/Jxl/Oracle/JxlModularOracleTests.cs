namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Decodes Modular JPEG XL files that libjxl just produced and requires the output to be byte-identical to
/// libjxl's own decode. The Modular path is pure integer arithmetic, so any difference is a real defect.
/// </summary>
[Trait("Category", "Oracle")]
public class JxlModularOracleTests
{
    // (lavfi source, input pix_fmt, our expected format, libjxl output pix_fmt)
    public static TheoryData<string, string, PixelFormat, string> Sources() => new()
    {
        { "testsrc2=size=64x48", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "testsrc2=size=64x48", "rgba", PixelFormat.Rgba32, "rgba" },
        { "testsrc2=size=64x48", "gray", PixelFormat.Gray8, "gray" },
        { "testsrc2=size=64x48", "ya8", PixelFormat.Rgba32, "rgba" },
        { "testsrc2=size=64x48", "rgb48le", PixelFormat.Rgb48, "rgb48le" },
        { "testsrc2=size=64x48", "gray16le", PixelFormat.Gray16, "gray16le" },
        { "testsrc2=size=64x48", "rgba64le", PixelFormat.Rgba64, "rgba64le" },
        { "testsrc2=size=37x29", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "color=c=red:size=1x1", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "color=c=0x336699:size=8x8", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "mandelbrot=size=300x200", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "mandelbrot=size=600x700", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "mandelbrot=size=513x259", "rgba", PixelFormat.Rgba32, "rgba" },
        { "rgbtestsrc=size=400x300", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "nullsrc=s=96x64,geq=r='random(1)*255':g='random(2)*255':b='random(3)*255'", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "nullsrc=s=700x40,geq=r='random(1)*255':g='random(2)*255':b='random(3)*255'", "rgb48le", PixelFormat.Rgb48, "rgb48le" },
        { "mandelbrot=size=257x131", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "mandelbrot=size=100x80,format=gray", "grayf32le", PixelFormat.GrayF32, "grayf32le" },
        { "gradients=size=320x240", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "smptebars=size=200x120", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "color=c=red:size=64x64", "rgb24", PixelFormat.Rgb24, "rgb24" },
        { "life=size=128x96:ratio=0.5:seed=3,format=gray", "gray", PixelFormat.Gray8, "gray" },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void LosslessModular_DefaultEffort_MatchesLibjxl(string source, string inputFormat, PixelFormat expected, string referenceFormat) =>
        AssertMatches(source, inputFormat, "-distance 0 -modular 1", expected, referenceFormat);

    [Theory]
    [InlineData("gbrpf32le", "rgb48le", PixelFormat.Rgb48)]
    [InlineData("gbrapf32le", "rgba64le", PixelFormat.Rgba64)]
    public void LosslessModularFromFloatInput_MatchesLibjxl(string inputFormat, string referenceFormat, PixelFormat expected)
    {
        // ffmpeg's libjxl encoder stores float input as 16-bit integer samples, so these files decode to 16-bit formats.
        AssertMatches("mandelbrot=size=100x80", inputFormat, "-distance 0 -modular 1", expected, referenceFormat);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(9)]
    public void LosslessModular_EveryEffort_MatchesLibjxl(int effort) =>
        AssertMatches("mandelbrot=size=300x200", "rgb24", $"-distance 0 -modular 1 -effort {effort}", PixelFormat.Rgb24, "rgb24");

    [Theory]
    [InlineData(1, "-distance 1")]
    [InlineData(3, "-distance 1")]
    [InlineData(7, "-distance 2")]
    [InlineData(9, "-distance 4")]
    public void LossyModularWithoutXyb_MatchesLibjxl(int effort, string distance) =>
        AssertMatches("mandelbrot=size=300x200", "rgb24", $"{distance} -modular 1 -xyb 0 -effort {effort}", PixelFormat.Rgb24, "rgb24");

    private static void AssertMatches(string source, string inputFormat, string options, PixelFormat expected, string referenceFormat)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] jxl = LibjxlOracle.Encode(source, inputFormat, options);
        byte[] reference = LibjxlOracle.Decode(jxl, referenceFormat);

        using var stream = new MemoryStream(jxl);
        using var image = Image.Load(stream);

        Assert.Equal(expected, image.PixelFormat);
        Assert.Equal(reference.Length, image.GetPixelSpan().Length);
        if (inputFormat == "gbrapf32le")
        {
            // ffmpeg stores this input with premultiplied (associated) alpha and returns the stored colour; this decoder returns
            // straight colour, so multiply it back before comparing (low alpha makes the round trip inexact by a level or so).
            var ours = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
            var theirs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(reference.AsSpan());
            for (int i = 0; i < ours.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    int premultiplied = (int)Math.Round(ours[i + c] * (ours[i + 3] / 65535.0));
                    Assert.True(Math.Abs(premultiplied - theirs[i + c]) <= 1, $"Pixel {i / 4} channel {c}: libjxl {theirs[i + c]}, PeachImage {premultiplied} (premultiplied).");
                }

                Assert.Equal(theirs[i + 3], ours[i + 3]);
            }

            return;
        }

        Assert.True(reference.AsSpan().SequenceEqual(image.GetPixelSpan()), FirstDifference(reference, image.GetPixelSpan()));
    }

    private static string FirstDifference(byte[] reference, ReadOnlySpan<byte> actual)
    {
        for (int i = 0; i < Math.Min(reference.Length, actual.Length); i++)
        {
            if (reference[i] != actual[i])
            {
                return $"First difference at byte {i}: libjxl {reference[i]}, PeachImage {actual[i]}.";
            }
        }

        return "Lengths differ.";
    }
}
