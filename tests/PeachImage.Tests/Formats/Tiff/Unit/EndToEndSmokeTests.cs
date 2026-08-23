using PeachImage.Tests.Formats.Tiff.Corpus;

namespace PeachImage.Tests.Formats.Tiff.Unit;

/// <summary>End-to-end smoke test for the actual scenario this decoder exists for: load a user-uploaded TIFF via the public API, inspect it, and re-encode it as JPEG.</summary>
public class EndToEndSmokeTests
{
    [Fact]
    public void LoadTiffAndSaveAsJpeg_RoundTripsThroughPublicApi()
    {
        string path = Path.Combine(CorpusPaths.ImazenRoot, "tiff-conformance", "valid", "flower-rgb-contig-08.tif");
        if (!File.Exists(path))
        {
            return; // Corpus not fetched in this environment; covered elsewhere.
        }

        using var image = Image.Load(path);
        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);

        using var identifyStream = File.OpenRead(path);
        var info = Image.Identify(identifyStream);
        Assert.Equal("tiff", info.FormatName);
        Assert.Equal(image.Width, info.Width);
        Assert.Equal(image.Height, info.Height);

        using var output = new MemoryStream();
        image.Save(output, "jpeg");
        Assert.True(output.Length > 0);

        output.Position = 0;
        using var reloaded = Image.Load(output);
        Assert.Equal(image.Width, reloaded.Width);
        Assert.Equal(image.Height, reloaded.Height);
    }
}
