using System.Globalization;
using System.Reflection;
using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Tests.Formats.Jxl.Corpus;
using PeachImage.Tests.Formats.Jxl.Unit;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Sample-for-sample parity with libjxl's reference decoder (<c>djxl</c>) over the whole JPEG XL corpus: the libjxl conformance
/// suite, the libjxl test data and every JPEG XL file checked into this test project. Both decoders write every visible frame in
/// the file's own colour encoding at 16 bits, so the comparison is free of the 8-bit rounding the PNG references and the ffmpeg
/// oracle carry. Skipped when <c>djxl</c> is not installed (set <c>PEACHIMAGE_DJXL</c> or put it on the PATH).
/// </summary>
[Trait("Category", "Oracle")]
public class JxlDjxlParityTests
{
    private const string AssetPrefix = "PeachImage.Tests.Formats.Jxl.Assets.";

    public static IEnumerable<TheoryDataRow<string>> Sources()
    {
        if (!DjxlOracle.IsAvailable)
        {
            yield return CorpusSkip.Row("djxl (libjxl's reference decoder) is not installed; set PEACHIMAGE_DJXL or add it to the PATH.");
            yield break;
        }

        foreach (string resource in typeof(JxlDjxlParityTests).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(AssetPrefix, StringComparison.Ordinal) && n.EndsWith(".jxl", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>("asset:" + resource[AssetPrefix.Length..]);
        }

        if (CorpusFixture.IsAvailable)
        {
            if (Directory.Exists(CorpusPaths.ConformanceRoot))
            {
                foreach (string file in Directory.EnumerateFiles(CorpusPaths.ConformanceRoot, "input.jxl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    // The "_5" directories hold deliberately truncated stubs.
                    if (!Path.GetFileName(Path.GetDirectoryName(file))!.EndsWith("_5", StringComparison.Ordinal))
                    {
                        yield return new TheoryDataRow<string>("corpus:" + file);
                    }
                }
            }

            if (Directory.Exists(CorpusPaths.TestDataRoot))
            {
                foreach (string file in Directory.EnumerateFiles(CorpusPaths.TestDataRoot, "*.jxl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    yield return new TheoryDataRow<string>("corpus:" + file);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Decode_MatchesDjxl(string source)
    {
        Assert.SkipUnless(DjxlOracle.IsAvailable, "djxl is not installed.");

        string name = source[(source.IndexOf(':', StringComparison.Ordinal) + 1)..];
        byte[] file = source.StartsWith("asset:", StringComparison.Ordinal) ? JxlTestAssets.Load(name) : File.ReadAllBytes(name);
        string shortName = source.StartsWith("asset:", StringComparison.Ordinal)
            ? name
            : Path.GetRelativePath(CorpusPaths.Root, name).Replace('\\', '/');

        var info = Image.Identify(new MemoryStream(file));
        Assert.SkipWhen(info.PixelFormat == PixelFormat.Cmyk32, $"{shortName}: CMYK is returned in its own pixel format.");

        var headers = JxlCodestreamHeaders.Read(JxlContainer.Parse(file).Codestream.ToArray());
        var metadata = headers.Metadata;

        // libjxl renders an XYB image with an embedded ICC profile to a different working space than the profile (the conformance
        // suite's reference images, which the corpus tests check, are in the profile's space), so there is nothing to compare here.
        Assert.SkipWhen(metadata.XybEncoded && metadata.ColorEncoding.WantIcc, $"{shortName}: an XYB image with an ICC profile is written by djxl in a different colour space than the profile; the conformance reference images cover it.");
        bool premultiplied = metadata.AlphaChannelIndex >= 0 && metadata.ExtraChannels[metadata.AlphaChannelIndex].AlphaAssociated;

        double worst;
        double meanOverFrames;
        if (info.IsAnimated)
        {
            (worst, meanOverFrames) = CompareAnimation(file, shortName);
        }
        else
        {
            (worst, meanOverFrames) = CompareStill(file, shortName, premultiplied);
        }

        string? reportPath = Environment.GetEnvironmentVariable("PEACHIMAGE_JXL_PARITY_REPORT");
        if (!string.IsNullOrEmpty(reportPath))
        {
            File.AppendAllText(reportPath, string.Create(CultureInfo.InvariantCulture, $"{shortName}\tmax={worst:E3}\tmean={meanOverFrames:E3}\n"));
        }

        var (maxAllowed, meanAllowed) = Tolerance(shortName);
        Assert.True(worst <= maxAllowed, $"{shortName}: the largest sample difference from djxl is {worst:E3} of full scale (allowed {maxAllowed:E3}).");
        Assert.True(meanOverFrames <= meanAllowed, $"{shortName}: the mean sample difference from djxl is {meanOverFrames:E3} of full scale (allowed {meanAllowed:E3}).");
    }

    // A still image: djxl's 16-bit PNG, compared with our 16-bit decode in the same pixel format.
    private static (double Max, double Mean) CompareStill(byte[] file, string name, bool premultiplied)
    {
        using var reference = DjxlOracle.DecodeToPng16(file, out string message);
        Assert.SkipWhen(reference is null, $"{name}: djxl cannot decode it ({message}).");

        PixelFormat format = reference!.PixelFormat;
        Assert.True(format.GetBytesPerSample() == 2, $"{name}: expected a 16-bit reference, got {format}.");
        Image ours;
        try
        {
            ours = Image.Load(new MemoryStream(file), new DecoderOptions { TargetPixelFormat = format });
        }
        catch (JxlUnsupportedFeatureException ex)
        {
            Assert.Skip($"{name}: not supported ({ex.Message})");
            return default;
        }

        using (ours)
        {
            Assert.Equal(reference.Width, ours.Width);
            Assert.Equal(reference.Height, ours.Height);
            int bytesPerRow = reference.Width * format.GetBytesPerPixel();
            int channels = format.GetChannelCount();

            // djxl keeps the colour of an image with associated (premultiplied) alpha premultiplied; this decoder, like the other
            // PeachImage codecs, returns straight alpha, so the colour is premultiplied again for the comparison.
            bool premultiply = premultiplied && channels == 4;
            double max = 0;
            double total = 0;
            long count = 0;
            for (int y = 0; y < reference.Height; y++)
            {
                var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(reference.GetRowSpan(y));
                var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(ours.GetRowSpan(y));
                Assert.Equal(bytesPerRow, ours.GetRowSpan(y).Length);
                for (int i = 0; i < a.Length; i++)
                {
                    int ourValue = b[i];
                    if (premultiply && (i & 3) != 3)
                    {
                        ourValue = (int)Math.Round(ourValue * (double)b[i | 3] / 65535.0);
                    }

                    double difference = Math.Abs(a[i] - ourValue) / 65535.0;
                    max = Math.Max(max, difference);
                    total += difference;
                    count++;
                }
            }

            return (max, total / count);
        }
    }

    // An animation: djxl's float samples for every composited frame, compared with our float decode.
    private static (double Max, double Mean) CompareAnimation(byte[] file, string name)
    {
        var reference = DjxlOracle.Decode(file, out string message);
        Assert.SkipWhen(reference is null, $"{name}: djxl cannot decode it ({message}).");
        Assert.SkipWhen(reference!.FrameCount == 0, $"{name}: djxl wrote no frames.");

        PixelFormat format = reference.Channels switch
        {
            1 => PixelFormat.GrayF32,
            3 => PixelFormat.RgbF32,
            _ => PixelFormat.RgbaF32,
        };
        List<Image> frames;
        try
        {
            frames = JxlDecoder.DecodeFrames(new MemoryStream(file), format);
        }
        catch (JxlUnsupportedFeatureException ex)
        {
            Assert.Skip($"{name}: not supported ({ex.Message})");
            return default;
        }

        try
        {
            Assert.Equal(reference.FrameCount, frames.Count);
            double worst = 0;
            double meanSum = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                var (max, mean) = Compare(reference, i, frames[i]);
                worst = Math.Max(worst, max);
                meanSum += mean;
            }

            return (worst, meanSum / frames.Count);
        }
        finally
        {
            frames.ForEach(image => image.Dispose());
        }
    }

    // Allowed difference from libjxl as a fraction of full scale. Almost everything agrees to within a couple of units of the 16-bit
    // output (1/65535 = 1.5e-5); the exceptions below are understood differences, not slack.
    private static (double Max, double Mean) Tolerance(string name) => Path.GetFileNameWithoutExtension(Path.GetDirectoryName(name) is { Length: > 0 } dir ? dir : name) switch
    {
        // BT.709 output: the encoding curve is evaluated with a different (fast) power approximation than libjxl's.
        "bike" => (4e-4, 1.5e-4),

        // Alpha that is blended or upsampled is kept at the alpha channel's own sample depth and in [0, 1] here, while libjxl keeps it as
        // an unclamped float (up to 1.03 after blending). The colour channels agree.
        "upsampling" or "conformance_upsampling" => (2.1e-3, 1.2e-4),
        "animation_icos4d" or "conformance_animation_icos4d" => (3.4e-2, 1.5e-4),
        "blendmodes" or "conformance_blendmodes" => (2e-4, 3e-5),

        // Premultiplied colour brighter than its alpha allows is clamped once it is returned as straight alpha, which premultiplying
        // again cannot restore; it affects a handful of pixels.
        "alpha_premultiplied" => (6e-3, 5e-5),

        _ => (6e-5, 2e-6),
    };

    // Largest and mean absolute difference over all samples (full scale is 1.0).
    private static (double Max, double Mean) Compare(FloatFrames reference, int frame, Image image)
    {
        Assert.Equal(reference.Width, image.Width);
        Assert.Equal(reference.Height, image.Height);
        int channels = image.PixelFormat.GetChannelCount();
        double max = 0;
        double total = 0;
        long count = 0;
        for (int y = 0; y < image.Height; y++)
        {
            var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetRowSpan(y));
            for (int x = 0; x < image.Width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    // Gray with alpha comes back from djxl as two channels and from us as RGBA.
                    int referenceChannel = reference.Channels == 2 ? (c == 3 ? 1 : 0) : c;
                    double difference = Math.Abs(row[(x * channels) + c] - reference.Sample(frame, x, y, referenceChannel));
                    max = Math.Max(max, difference);
                    total += difference;
                    count++;
                }
            }
        }

        return (max, total / count);
    }
}
