namespace PeachImage.Tests.Formats.Tiff.Unit;

/// <summary>Decodes CCITT Group 3 / Group 4 / Modified Huffman TIFFs written by libtiff and compares them against the source bitmaps.</summary>
public class TiffCcittTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        string[] images = ["noise", "runs", "similar", "sparse", "white", "black", "alt", "tiny"];
        string[] variants = ["g4", "g3", "g3_2d", "g3_fill", "mh", "g4_lsb", "g4_strips"];
        foreach (string image in images)
        {
            foreach (string variant in variants)
            {
                data.Add(image, variant);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decode_MatchesSourceBitmap(string image, string variant)
    {
        using var expected = Image.Load(ReadAsset($"{image}.png"));
        using var actual = Image.Load(ReadAsset($"{image}_{variant}.tif"));

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(
                expected.GetRowSpan(y).SequenceEqual(actual.GetRowSpan(y)),
                $"{image}_{variant}: row {y} differs.");
        }
    }

    [Fact]
    public void Identify_ReportsCcittAsLossless()
    {
        using var stream = new MemoryStream(ReadAsset("runs_g4.tif"));
        var info = Image.Identify(stream);
        Assert.Equal("tiff", info.FormatName);
        Assert.True(info.IsLosslessEncoding);
        Assert.Equal(2700, info.Width);
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(TiffCcittTests).Assembly.GetManifestResourceStream($"PeachImage.Tests.Formats.Tiff.Assets.Ccitt.{name}")
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
