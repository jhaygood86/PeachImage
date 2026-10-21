namespace PeachImage.Tests.Formats.Jpeg2000.Unit;

/// <summary>Decodes files written by OpenJPEG (through Pillow) and compares them with OpenJPEG's own decode.</summary>
public class Jpeg2000DecodeTests
{
    private const string Prefix = "PeachImage.Tests.Formats.Jpeg2000.Assets.";

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (string resource in typeof(Jpeg2000DecodeTests).Assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string file = resource[Prefix.Length..];
            if ((file.EndsWith(".jp2", StringComparison.Ordinal) || file.EndsWith(".j2k", StringComparison.Ordinal))
                && typeof(Jpeg2000DecodeTests).Assembly.GetManifestResourceNames().Contains(Prefix + file[..file.LastIndexOf('.')] + ".png"))
            {
                data.Add(file, file[..file.LastIndexOf('.')]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decode_MatchesOpenJpeg(string file, string name)
    {
        using var expected = Image.Load(ReadAsset($"{name}.png"));
        using var actual = Image.Load(ReadAsset(file));

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.PixelFormat, actual.PixelFormat);

        // The irreversible 9/7 path rounds in float, so allow one level of difference there; reversible files must be exact.
        int tolerance = name.Contains("lossy", StringComparison.Ordinal) ? 1 : name.Contains("sycc", StringComparison.Ordinal) ? 2 : 0;
        int worst = 0;
        int mismatches = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            var a = expected.GetRowSpan(y);
            var b = actual.GetRowSpan(y);
            if (expected.PixelFormat is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64)
            {
                var wa = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(a);
                var wb = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(b);
                for (int x = 0; x < wa.Length; x++)
                {
                    int d = Math.Abs(wa[x] - wb[x]);
                    worst = Math.Max(worst, d);
                    if (d > tolerance)
                    {
                        mismatches++;
                    }
                }
            }
            else
            {
                for (int x = 0; x < a.Length; x++)
                {
                    int d = Math.Abs(a[x] - b[x]);
                    worst = Math.Max(worst, d);
                    if (d > tolerance)
                    {
                        mismatches++;
                    }
                }
            }
        }

        Assert.True(mismatches == 0, $"{file}: {mismatches} samples differ by more than {tolerance} (worst {worst}).");
    }

    [Fact]
    public void Identify_ReportsDimensionsAndLosslessness()
    {
        using var stream = new MemoryStream(ReadAsset("rgb_lossless.jp2"));
        var info = Image.Identify(stream);
        Assert.Equal("jpeg2000", info.FormatName);
        Assert.Equal(150, info.Width);
        Assert.Equal(110, info.Height);
        Assert.Equal(PixelFormat.Rgb24, info.PixelFormat);
        Assert.True(info.IsLosslessEncoding);

        using var lossy = new MemoryStream(ReadAsset("rgb_lossy.jp2"));
        Assert.False(Image.Identify(lossy).IsLosslessEncoding);
    }

    [Fact]
    public void Decode_ReducedResolution_HalvesTheSize()
    {
        using var full = Image.Load(ReadAsset("rgb_lossless.jp2"));
        using var half = Image.Load(ReadAsset("rgb_lossless.jp2"), new PeachImage.Formats.Jpeg2000.Jpeg2000DecoderOptions { ReduceResolutionLevels = 1 });
        Assert.Equal((full.Width + 1) / 2, half.Width);
        Assert.Equal((full.Height + 1) / 2, half.Height);
    }

    [Fact]
    public void Decode_Truncated_ThrowsOrDecodesPartially()
    {
        byte[] data = ReadAsset("rgb_lossless.jp2");
        try
        {
            using var image = Image.Load(data.AsSpan(0, data.Length / 2));
            Assert.Equal(150, image.Width);
        }
        catch (ImageFormatException)
        {
        }
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(Jpeg2000DecodeTests).Assembly.GetManifestResourceStream($"{Prefix}{name}")
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
