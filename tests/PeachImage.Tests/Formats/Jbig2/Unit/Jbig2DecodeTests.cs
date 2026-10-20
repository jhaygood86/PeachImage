using PeachImage.Formats.Jbig2;

namespace PeachImage.Tests.Formats.Jbig2.Unit;

/// <summary>Decodes streams written by Assets/jbig2gen.py, both as standalone files and as PDF-style embedded streams.</summary>
public class Jbig2DecodeTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        var assembly = typeof(Jbig2DecodeTests).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            const string prefix = "PeachImage.Tests.Formats.Jbig2.Assets.";
            if (resource.StartsWith(prefix, StringComparison.Ordinal) && resource.EndsWith(".png", StringComparison.Ordinal))
            {
                data.Add(resource[prefix.Length..^4]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodeStandaloneFile_MatchesIntendedPage(string name)
    {
        using var expected = Image.Load(ReadAsset($"{name}.png"));
        using var actual = Image.Load(ReadAsset($"{name}.jb2"));
        AssertSame(expected, actual, name);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodeEmbeddedStream_MatchesIntendedPage(string name)
    {
        using var expected = Image.Load(ReadAsset($"{name}.png"));
        byte[]? globals = TryReadAsset($"{name}.glb");
        var options = new Jbig2DecoderOptions { Embedded = true, Globals = globals };
        using var actual = Image.Load(new MemoryStream(ReadAsset($"{name}.seg")), options);
        AssertSame(expected, actual, name);
    }

    [Fact]
    public void Identify_ReportsPageSize()
    {
        using var stream = new MemoryStream(ReadAsset("generic_t0.jb2"));
        var info = Image.Identify(stream);
        Assert.Equal("jbig2", info.FormatName);
        Assert.Equal(203, info.Width);
        Assert.Equal(70, info.Height);
        Assert.True(info.IsLosslessEncoding);
    }

    [Fact]
    public void InvertOutput_SwapsBlackAndWhite()
    {
        using var normal = Image.Load(ReadAsset("generic_t0.jb2"));
        using var inverted = Image.Load(ReadAsset("generic_t0.jb2"), new Jbig2DecoderOptions { InvertOutput = true });
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
    public void Truncated_Throws()
    {
        byte[] data = ReadAsset("text_topleft.jb2");
        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(data.AsSpan(0, data.Length / 2)));
    }

    private static void AssertSame(Image expected, Image actual, string name)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(actual.GetRowSpan(y)), $"{name}: row {y} differs.");
        }
    }

    private static byte[] ReadAsset(string name) =>
        TryReadAsset(name) ?? throw new InvalidOperationException($"Missing test asset {name}.");

    private static byte[]? TryReadAsset(string name)
    {
        using var stream = typeof(Jbig2DecodeTests).Assembly.GetManifestResourceStream($"PeachImage.Tests.Formats.Jbig2.Assets.{name}");
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
