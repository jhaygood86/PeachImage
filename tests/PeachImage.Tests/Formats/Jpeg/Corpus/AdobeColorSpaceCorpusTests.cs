using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Decoding;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// Targeted tests against four real, already-fetched Imazen <c>codec-corpus</c> fixtures that exercise Adobe
/// APP14 color-space resolution end to end: <c>ycck.jpg</c>/<c>cmyk_logo.jpg</c> (Adobe transform=2, YCCK),
/// <c>cymk.jpg</c> (Adobe transform=0, Adobe-inverted direct CMYK), and <c>rgb.jpg</c> (Adobe transform=0,
/// direct RGB). Unlike <see cref="ImazenConformanceCorpusTests"/>'s broad sweep, these assert real pixel-level
/// and metadata-level correctness for the specific paths those files are known (by construction, confirmed
/// during investigation) to hit.
/// </summary>
public class AdobeColorSpaceCorpusTests
{
    private static string ValidPath(string fileName) => Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "valid", fileName);

    public static TheoryData<string, PixelFormat, bool, bool> Fixtures() => new()
    {
        // fileName, expectedPixelFormat, expectedIsAdobeInvertedCmyk, expectedIsYcck
        { "cymk.jpg", PixelFormat.Cmyk32, true, false },
        { "ycck.jpg", PixelFormat.Cmyk32, false, true },
        { "cmyk_logo.jpg", PixelFormat.Cmyk32, false, true },
        { "rgb.jpg", PixelFormat.Rgb24, false, false },
    };

    public static TheoryData<string, PixelFormat> FixturesForDecode() => new()
    {
        { "cymk.jpg", PixelFormat.Cmyk32 },
        { "ycck.jpg", PixelFormat.Cmyk32 },
        { "cmyk_logo.jpg", PixelFormat.Cmyk32 },
        { "rgb.jpg", PixelFormat.Rgb24 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Identify_ReportsExpectedPixelFormatAndAdobeFlags(string fileName, PixelFormat expectedFormat, bool expectedInverted, bool expectedYcck)
    {
        string path = ValidPath(fileName);
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var stream = File.OpenRead(path);
        var info = Image.Identify(stream);

        Assert.Equal(expectedFormat, info.PixelFormat);
        Assert.Equal(expectedInverted, info.IsAdobeInvertedCmyk);
        Assert.Equal(expectedYcck, info.IsYcck);
    }

    [Theory]
    [MemberData(nameof(FixturesForDecode))]
    public void Decode_ProducesExpectedPixelFormatAndNoAlpha(string fileName, PixelFormat expectedFormat)
    {
        string path = ValidPath(fileName);
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var stream = File.OpenRead(path);
        using var image = JpegDecoder.Decode(stream);

        Assert.Equal(expectedFormat, image.PixelFormat);
        Assert.False(image.HasAlpha);
    }

    [Fact]
    public void KeepAdobeCmykInverted_YieldsComplementOfNormalizedDecode()
    {
        string path = ValidPath("cymk.jpg");
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var normalized = Decode(path, new JpegDecoderOptions());
        using var inverted = Decode(path, new JpegDecoderOptions { KeepAdobeCmykInverted = true });

        var normalizedSpan = normalized.GetPixelSpan();
        var invertedSpan = inverted.GetPixelSpan();
        Assert.Equal(normalizedSpan.Length, invertedSpan.Length);
        for (int i = 0; i < normalizedSpan.Length; i++)
        {
            Assert.Equal((byte)(255 - normalizedSpan[i]), invertedSpan[i]);
        }
    }

    [Theory]
    [InlineData("ycck.jpg")]
    [InlineData("cmyk_logo.jpg")]
    public void DecodeRawYcck_YieldsYcck32WithMatchingKChannelAndDifferingCmy(string fileName)
    {
        string path = ValidPath(fileName);
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var converted = Decode(path, new JpegDecoderOptions());
        using var raw = Decode(path, new JpegDecoderOptions { DecodeRawYcck = true });

        Assert.Equal(PixelFormat.Ycck32, raw.PixelFormat);
        Assert.Equal(PixelFormat.Cmyk32, converted.PixelFormat);

        var convertedSpan = converted.GetPixelSpan();
        var rawSpan = raw.GetPixelSpan();
        Assert.Equal(convertedSpan.Length, rawSpan.Length);

        bool anyCmyDiffers = false;
        for (int i = 0; i < convertedSpan.Length; i += 4)
        {
            // K (4th channel) is untouched by the YCbCr->CMY transform either way -- must match exactly.
            Assert.Equal(convertedSpan[i + 3], rawSpan[i + 3]);

            if (convertedSpan[i] != rawSpan[i] || convertedSpan[i + 1] != rawSpan[i + 1] || convertedSpan[i + 2] != rawSpan[i + 2])
            {
                anyCmyDiffers = true;
            }
        }

        Assert.True(anyCmyDiffers, "Expected raw YCCK planes to differ from the converted CMY output for at least one pixel.");
    }

    [Fact]
    public void KeepAdobeCmykInverted_WithNonCmykTargetFormat_Throws()
    {
        string path = ValidPath("cymk.jpg");
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var stream = File.OpenRead(path);
        Assert.Throws<JpegDecodingException>(() => JpegDecoder.Decode(
            stream,
            new JpegDecoderOptions { KeepAdobeCmykInverted = true, TargetPixelFormat = PixelFormat.Rgba32 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PixelFormat.Cmyk32)]
    public void KeepAdobeCmykInverted_WithCmykOrNullTargetFormat_DoesNotThrow(PixelFormat? targetFormat)
    {
        string path = ValidPath("cymk.jpg");
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var stream = File.OpenRead(path);
        using var image = JpegDecoder.Decode(stream, new JpegDecoderOptions { KeepAdobeCmykInverted = true, TargetPixelFormat = targetFormat });
        Assert.Equal(PixelFormat.Cmyk32, image.PixelFormat);
    }

    [Fact]
    public void IccAwareConversion_EngagesForYcckFixtureWithEmbeddedProfile()
    {
        // ycck.jpg carries a genuine embedded ICC profile (confirmed during investigation) -- decoding it with
        // TargetPixelFormat=Rgba32 should use IccDeviceToSrgbConverter's colorimetric path, not the naive
        // additive formula, so the two should disagree for at least some pixels.
        string path = ValidPath("ycck.jpg");
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var cmykImage = Decode(path, new JpegDecoderOptions());
        using var iccImage = Decode(path, new JpegDecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.Equal(PixelFormat.Rgba32, iccImage.PixelFormat);

        int pixelCount = cmykImage.Width * cmykImage.Height;
        var naiveRgba = new byte[pixelCount * 4];
        PixelFormatConversionKernels.ConvertCmyk32ToRgba32(cmykImage.GetPixelSpan(), naiveRgba, pixelCount);

        var iccRgba = iccImage.GetPixelSpan();
        bool anyPixelDiffers = false;
        for (int i = 0; i < naiveRgba.Length; i++)
        {
            if (naiveRgba[i] != iccRgba[i])
            {
                anyPixelDiffers = true;
                break;
            }
        }

        Assert.True(anyPixelDiffers, "Expected the ICC-aware conversion to differ from the naive additive formula for at least one byte.");
    }

    [Fact]
    public void IccAwareConversion_FallsBackGracefullyOnCorruptProfile()
    {
        // cymk.jpg has no real embedded ICC profile, so injecting a deliberately-corrupt one lets this
        // confirm the fallback path itself: PixelFormatConverter must still produce the same output as the
        // naive kernel, and must not throw.
        string path = ValidPath("cymk.jpg");
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var cmykImage = Decode(path, new JpegDecoderOptions());
        int pixelCount = cmykImage.Width * cmykImage.Height;
        var naiveRgba = new byte[pixelCount * 4];
        PixelFormatConversionKernels.ConvertCmyk32ToRgba32(cmykImage.GetPixelSpan(), naiveRgba, pixelCount);

        cmykImage.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = new byte[10] });
        var fallbackImage = PixelFormatConverter.ConvertIfNeeded(cmykImage, PixelFormat.Rgba32);

        Assert.Equal(PixelFormat.Rgba32, fallbackImage.PixelFormat);
        Assert.True(naiveRgba.AsSpan().SequenceEqual(fallbackImage.GetPixelSpan()));
    }

    private static Image Decode(string path, JpegDecoderOptions options)
    {
        using var stream = File.OpenRead(path);
        return JpegDecoder.Decode(stream, options);
    }
}
