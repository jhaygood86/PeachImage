using PeachImage.Formats.Ccitt;

namespace PeachImage.Tests.Formats.Tiff.Unit;

/// <summary>Decodes bare CCITT streams (as stored in a PDF CCITTFaxDecode filter) through <see cref="Image.Load(Stream, DecoderOptions?)"/>.</summary>
public class RawCcittTests
{
    [Theory]
    [InlineData("similar", "g4", -1, 1000)]
    [InlineData("runs", "g3", 0, 2700)]
    [InlineData("similar", "g3_2d", 1, 1000)]
    [InlineData("noise", "g3_fill", 0, 211)]
    public void Load_WithFaxOptions_MatchesSource(string image, string variant, int k, int columns)
    {
        using var expected = Image.Load(ReadAsset($"{image}.png"));
        using var stream = new MemoryStream(ReadAsset($"{image}_{variant}.raw"));
        var options = new CcittFaxDecoderOptions
        {
            K = k,
            Columns = columns,
            Rows = expected.Height,
            BlackIs1 = true, // libtiff writes the bitmap's 1 (white) pixels as black runs.
        };

        using var actual = Image.Load(stream, options);

        Assert.Equal(PixelFormat.Gray8, actual.PixelFormat);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(actual.GetRowSpan(y)), $"row {y} differs: exp {string.Join(",", expected.GetRowSpan(y).Slice(0, 12).ToArray())} act {string.Join(",", actual.GetRowSpan(y).Slice(0, 12).ToArray())}");
        }
    }

    [Fact]
    public void Load_BlackIs1_InvertsOutput()
    {
        using var normal = Image.Load(new MemoryStream(ReadAsset("similar_g4.raw")), new CcittFaxDecoderOptions { K = -1, Columns = 1000, Rows = 120 });
        using var inverted = Image.Load(new MemoryStream(ReadAsset("similar_g4.raw")), new CcittFaxDecoderOptions { K = -1, Columns = 1000, Rows = 120, BlackIs1 = true });

        for (int y = 0; y < normal.Height; y++)
        {
            var a = normal.GetRowSpan(y);
            var b = inverted.GetRowSpan(y);
            for (int x = 0; x < a.Length; x++)
            {
                Assert.Equal((byte)~a[x], b[x]);
            }
        }
    }

    [Fact]
    public void Load_UnknownRowCount_DecodesUntilEndOfData()
    {
        using var actual = Image.Load(new MemoryStream(ReadAsset("similar_g4.raw")), new CcittFaxDecoderOptions { K = -1, Columns = 1000 });
        Assert.Equal(120, actual.Height);
    }

    [Fact]
    public void Load_TruncatedData_Throws()
    {
        byte[] data = ReadAsset("similar_g4.raw");
        var options = new CcittFaxDecoderOptions { K = -1, Columns = 1000, Rows = 120 };
        Assert.Throws<CcittFaxDecodingException>(() => Image.Load(new MemoryStream(data, 0, data.Length / 2), options));
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(RawCcittTests).Assembly.GetManifestResourceStream($"PeachImage.Tests.Formats.Tiff.Assets.Ccitt.{name}")
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
