using PeachImage.Formats.Jxl;
using PeachImage.Tests.Formats.Jxl.Unit;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>Truncated and corrupted streams must fail with a JPEG XL exception (or decode), never crash, hang or throw anything else.</summary>
public class JxlRobustnessTests
{
    [Theory]
    [InlineData("rgb_lossless.jxl")]
    [InlineData("rgba.jxl")]
    [InlineData("gray.jxl")]
    [InlineData("rgb16.jxl")]
    [InlineData("icc_lossless.jxl")]
    [InlineData("conformance_splines.jxl")]
    [InlineData("testdata_cropped_traffic_light.jxl")]
    public void EveryTruncation_FailsCleanly(string asset)
    {
        byte[] bytes = JxlTestAssets.Load(asset);
        for (int length = 0; length < bytes.Length; length++)
        {
            AssertCleanFailureOrSuccess(bytes.AsSpan(0, length).ToArray());
        }
    }

    [Theory]
    [InlineData("rgb_lossless.jxl")]
    [InlineData("rgba.jxl")]
    [InlineData("icc_lossless.jxl")]
    public void RandomByteCorruption_FailsCleanly(string asset)
    {
        byte[] original = JxlTestAssets.Load(asset);
        var random = new Random(12345);
        for (int trial = 0; trial < 400; trial++)
        {
            byte[] corrupted = (byte[])original.Clone();
            int flips = 1 + random.Next(3);
            for (int i = 0; i < flips; i++)
            {
                corrupted[random.Next(corrupted.Length)] ^= (byte)(1 << random.Next(8));
            }

            AssertCleanFailureOrSuccess(corrupted);
        }
    }

    [Theory]
    [InlineData("conformance_patches_lossless.jxl")]
    [InlineData("conformance_noise.jxl")]
    public void LargeFrameFeatureFiles_SampledTruncationsAndCorruptionFailCleanly(string asset)
    {
        byte[] original = JxlTestAssets.Load(asset);
        var random = new Random(777);
        for (int trial = 0; trial < 25; trial++)
        {
            AssertCleanFailureOrSuccess(original.AsSpan(0, random.Next(original.Length)).ToArray());

            // Corrupt the start of the file, where the headers and the global section (patches, noise) live.
            byte[] corrupted = (byte[])original.Clone();
            corrupted[random.Next(Math.Min(corrupted.Length, 600))] ^= (byte)(1 << random.Next(8));
            AssertCleanFailureOrSuccess(corrupted);
        }
    }

    private static void AssertCleanFailureOrSuccess(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            using var image = Image.Load(stream);
        }
        catch (JxlFormatException)
        {
        }
        catch (UnknownImageFormatException)
        {
        }
    }
}
