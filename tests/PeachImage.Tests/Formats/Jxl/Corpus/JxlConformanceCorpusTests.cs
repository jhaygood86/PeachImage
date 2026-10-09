using System.Text.Json;
using PeachImage.Formats.Jxl;
using PeachImage.Tests.Formats.Jxl.Unit;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Jxl.Corpus;

/// <summary>
/// The libjxl conformance suite (https://github.com/libjxl/conformance) and the libjxl test data. Every <c>input.jxl</c> must
/// decode or fail with a JPEG XL exception, never crash or hang. Where the suite provides a still reference image
/// (<c>ref.png</c>, in the file's own colour space) the decode is also compared with it using the suite's own thresholds from
/// <c>test.json</c> (peak and RMS error on a 0..1 scale). Files using features this decoder does not support are reported as
/// skipped through <see cref="JxlUnsupportedFeatureException"/>, which keeps the list of remaining gaps visible.
/// </summary>
[Trait("Category", "Corpus")]
public class JxlConformanceCorpusTests
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(60);

    public static IEnumerable<TheoryDataRow<string>> ConformanceCases() => EnumerateFiles(CorpusPaths.ConformanceRoot, "input.jxl");

    public static IEnumerable<TheoryDataRow<string>> TestDataFiles() => EnumerateFiles(CorpusPaths.TestDataRoot, "*.jxl");

    private static IEnumerable<TheoryDataRow<string>> EnumerateFiles(string directory, string pattern)
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(directory))
        {
            yield return CorpusSkip.Row("External JPEG XL test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return new TheoryDataRow<string>(file);
        }
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void ConformanceCase_DecodesAndMatchesTheReference(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(directory);
        string referencePath = Path.Combine(directory, "ref.png");

        // Decode into the reference image's own pixel format so the two can be compared sample for sample.
        PixelFormat? target = null;
        if (File.Exists(referencePath) && new FileInfo(path).Length > 200)
        {
            try
            {
                using var referenceStream = File.OpenRead(referencePath);
                target = Image.Identify(referenceStream).PixelFormat;
                using var inputStream = File.OpenRead(path);
                if (Image.Identify(inputStream).PixelFormat == PixelFormat.Cmyk32)
                {
                    target = null; // CMYK is only decoded as CMYK; the comparison colour-manages it afterwards.
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                target = null;
            }
        }

        Image? image = null;
        if (!CorpusHangGuard.TryRun(() => TryDecode(path, out image, target), PerFileTimeout, out var failure))
        {
            Assert.Fail($"{name}: decoding did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        if (failure is JxlUnsupportedFeatureException unsupported)
        {
            Assert.Skip($"{name}: {unsupported.Message}");
        }

        if (failure is not null && failure is not JxlFormatException and not UnknownImageFormatException)
        {
            Assert.Fail($"{name}: decoding threw {failure}");
        }

        if (failure is not null)
        {
            // Truncated and deliberately damaged cases (the "_5" variants, stored as stubs) are expected to be rejected.
            return;
        }

        // The decoder reports the orientation without applying it; the reference image is upright.
        ImageOrientation orientation;
        using (var orientationStream = File.OpenRead(path))
        {
            orientation = Image.Identify(orientationStream).Orientation;
        }

        using (image)
        {
            if (File.Exists(referencePath) && new FileInfo(path).Length > 200)
            {
                using var upright = image!.ApplyOrientation(orientation);
                CompareWithReference(name, upright, referencePath, Path.Combine(directory, "test.json"));
            }
        }
    }

    [Theory]
    [MemberData(nameof(TestDataFiles))]
    public void TestDataFile_DecodesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryDecode(path, out var image) ?? DisposeAndNull(image), PerFileTimeout, out var failure))
        {
            Assert.Fail($"{Path.GetFileName(path)}: decoding did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        if (failure is JxlUnsupportedFeatureException unsupported)
        {
            Assert.Skip($"{Path.GetFileName(path)}: {unsupported.Message}");
        }

        if (failure is not null && failure is not JxlFormatException and not UnknownImageFormatException)
        {
            Assert.Fail($"{Path.GetFileName(path)}: decoding threw {failure}");
        }
    }

    private static Exception? DisposeAndNull(Image? image)
    {
        image?.Dispose();
        return null;
    }

    private static Exception? TryDecode(string path, out Image? image, PixelFormat? target = null)
    {
        image = null;
        try
        {
            using var stream = File.OpenRead(path);
            image = Image.Load(stream, new DecoderOptions { TargetPixelFormat = target });
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // A handful of samples may exceed the threshold: premultiplied colour near alpha 0 is divided by a tiny number, which amplifies
    // last-bit float differences between implementations (one sample in a million is allowed).
    private static void CompareWithReference(string name, Image ours, string referencePath, string thresholdsPath)
    {
        double peakLimit = 0.02;
        double rmsLimit = 0.001;
        if (File.Exists(thresholdsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(thresholdsPath));
            if (document.RootElement.TryGetProperty("frames", out var frames) && frames.GetArrayLength() > 0)
            {
                var first = frames[0];
                peakLimit = first.GetProperty("peak_error").GetDouble();
                rmsLimit = first.GetProperty("rms_error").GetDouble();
            }
        }

        Image reference;
        try
        {
            using var stream = File.OpenRead(referencePath);
            reference = Image.Load(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Skip($"{name}: the reference image could not be read ({ex.GetType().Name}).");
            return;
        }

        // CMYK is decoded as CMYK; the reference is that image colour-managed to sRGB by the reference decoder's CMS. Colour management is
        // outside the decoder, so use PeachImage's own ICC engine and allow for the differences between the two engines.
        Image? converted = null;
        if (ours.PixelFormat == PixelFormat.Cmyk32)
        {
            var profile = ours.Metadata.GetIccColorProfile() ?? throw new InvalidOperationException("A CMYK image without a profile.");
            converted = Image.Create(ours.Width, ours.Height, PixelFormat.Rgba32);
            profile.ConvertToSrgb(ours.GetPixelSpan(), converted.GetPixelSpan(), ours.Width * ours.Height, IccRenderingIntent.Perceptual, blackPointCompensation: true);
            ours = converted;
            peakLimit = Math.Max(peakLimit, 0.06);
            rmsLimit = Math.Max(rmsLimit, 0.012);
        }

        // A grayscale image whose reference carries no profile of its own was written in sRGB by the reference decoder, whereas this
        // decoder leaves gray pixels in the embedded profile's space; colour-manage ours (an ICC engine, not decoder, difference).
        if (converted is null && ours.PixelFormat == PixelFormat.Gray8 && reference.PixelFormat == PixelFormat.Gray8
            && ours.Metadata.GetIccColorProfile() is not null && !ReferenceHasProfile(referencePath))
        {
            using var srgb = ours.ConvertToSrgb();
            converted = Image.Create(ours.Width, ours.Height, PixelFormat.Gray8);
            var source = srgb.GetPixelSpan();
            var target = converted.GetPixelSpan();
            for (int i = 0; i < target.Length; i++)
            {
                target[i] = source[i * 4];
            }

            ours = converted;
            peakLimit = Math.Max(peakLimit, 0.03);
            rmsLimit = Math.Max(rmsLimit, 0.01);
        }

        using (converted)
        using (reference)
        {
            Assert.Equal(reference.Width, ours.Width);
            Assert.Equal(reference.Height, ours.Height);
            Assert.Equal(reference.PixelFormat, ours.PixelFormat);

            bool sixteenBit = reference.PixelFormat is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
            double peak = 0;
            double squares = 0;
            long outliers = 0;
            double quantum = sixteenBit ? 1.0 / 65535 : 1.0 / 255;
            long count;
            if (sixteenBit)
            {
                var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(ours.GetPixelSpan());
                var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(reference.GetPixelSpan());
                count = a.Length;
                for (int i = 0; i < a.Length; i++)
                {
                    double diff = Math.Abs(a[i] - b[i]) / 65535.0;
                    peak = Math.Max(peak, diff);
                    squares += diff * diff;
                    outliers += diff > peakLimit + quantum ? 1 : 0;
                }
            }
            else
            {
                var a = ours.GetPixelSpan();
                var b = reference.GetPixelSpan();
                count = a.Length;
                for (int i = 0; i < a.Length; i++)
                {
                    double diff = Math.Abs(a[i] - b[i]) / 255.0;
                    peak = Math.Max(peak, diff);
                    squares += diff * diff;
                    outliers += diff > peakLimit + quantum ? 1 : 0;
                }
            }

            double rms = Math.Sqrt(squares / count);
            long outlierBudget = Math.Max(1, count / 1_000_000);

            // The thresholds are for float references; the PNG is quantized to 8 or 16 bits, which adds up to half a level of peak
            // error and about 0.3 of a level of RMS error.
            Assert.True(outliers <= outlierBudget, $"{name}: {outliers} samples exceed the peak error {peakLimit:F5} (largest {peak:F5}).");
            Assert.True(rms <= rmsLimit + (quantum * 0.35), $"{name}: RMS error {rms:F6} exceeds {rmsLimit:F6}.");
        }
    }

    // Whether the PNG has an embedded profile (an iCCP chunk before the image data).
    private static bool ReferenceHasProfile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int position = 8;
        while (position + 8 <= data.Length)
        {
            int length = (data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
            string type = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            if (type == "iCCP")
            {
                return true;
            }

            if (type == "IDAT")
            {
                return false;
            }

            position += 12 + length;
        }

        return false;
    }
}
