using System.Security.Cryptography;
using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// Pins the exact decoded pixels of the 1080p benchmark assets (<c>bench/PeachImage.Benchmarks/Assets</c>). The
/// libavif corpus behind <see cref="AvifDecodeHashTests"/> is made of tiny images, so it never reaches the
/// large-frame paths -- threaded tile reconstruction, parallel filter bands, 128x128 superblocks, loop
/// restoration on a real photo, 10-bit output -- which are exactly what performance work rewrites. A mismatch
/// is a decoder change to look at: regenerate the expected values only when the change is intentional.
/// </summary>
public class AvifLargeAssetHashTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["photo_1920x1080_420.avif"] = "1920x1080:Rgb24:494FB737828CE94D4111FEBFAF7C96B2A443E058328982B3406F4E6BCCF89A39",
        ["photo_1920x1080_alpha.avif"] = "1920x1080:Rgb24:988231F3E566D1A02A08993A12669BAC230B145D5F5382BEE2AAE16F395D17E9",
        ["photo_1920x1080_lossless.avif"] = "1920x1080:Rgb24:494FB737828CE94D4111FEBFAF7C96B2A443E058328982B3406F4E6BCCF89A39",
        ["photo_1920x1080_420_lr.avif"] = "1920x1080:Rgb24:5DFDEE544A0D99E3CB62775D9BF984161F819E17E86314620FF304A897B584BC",
        ["photo_1920x1080_420_10bit_lr.avif"] = "1920x1080:Rgb48:BFF7515F400FAF01C06A0C25E4F27F89F6FEFE31D16D16EC2E78353D62C8EC15",
    };

    [Theory]
    [InlineData("photo_1920x1080_420.avif")]
    [InlineData("photo_1920x1080_alpha.avif")]
    [InlineData("photo_1920x1080_lossless.avif")]
    [InlineData("photo_1920x1080_420_lr.avif")]
    [InlineData("photo_1920x1080_420_10bit_lr.avif")]
    public void Decode_ProducesTheRecordedPixels(string asset)
    {
        string? path = FindAsset(asset);
        Assert.SkipWhen(path is null, $"Benchmark asset {asset} is not available.");

        using var stream = File.OpenRead(path!);
        using var image = AvifDecoder.Decode(stream);
        string actual = $"{image.Width}x{image.Height}:{image.PixelFormat}:{Convert.ToHexString(SHA256.HashData(image.GetPixelSpan()))}";

        Assert.True(Expected.TryGetValue(asset, out string? expected), $"No expected value recorded for {asset}: {actual}");
        Assert.Equal(expected, actual);
    }

    private static string? FindAsset(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "bench", "PeachImage.Benchmarks", "Assets", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
