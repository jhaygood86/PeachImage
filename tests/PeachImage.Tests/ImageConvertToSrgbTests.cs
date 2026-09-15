using PeachImage.Formats.Jpeg;
using PeachImage.Tests.Formats.Jpeg.Corpus;
using PeachImage.Tests.Internal.Icc;

namespace PeachImage.Tests;

/// <summary>
/// Tests <see cref="ImageMetadata.GetIccColorProfile"/> and <see cref="Image.ConvertToSrgb"/> — the high-level
/// public ICC convenience layer over <see cref="IccColorProfile"/>.
/// </summary>
public class ImageConvertToSrgbTests
{
    private static Image CreateImageWithIccProfile(int width, int height, PixelFormat format, byte[] fillPixel, byte[] iccProfileBytes)
    {
        var image = Image.Create(width, height, format);
        var span = image.GetPixelSpan();
        for (int i = 0; i < span.Length; i += fillPixel.Length)
        {
            fillPixel.CopyTo(span[i..]);
        }

        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = iccProfileBytes });
        return image;
    }

    [Fact]
    public void GetIccColorProfile_ReturnsNull_WhenNoProfilePresent()
    {
        using var image = Image.Create(2, 2, PixelFormat.Rgb24);
        Assert.Null(image.Metadata.GetIccColorProfile());
    }

    [Fact]
    public void GetIccColorProfile_ReturnsNull_ForCorruptEmbeddedProfile()
    {
        using var image = Image.Create(2, 2, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = new byte[10] });
        Assert.Null(image.Metadata.GetIccColorProfile());
    }

    [Fact]
    public void GetIccColorProfile_ReturnsWorkingProfile_ForSyntheticRgbProfile()
    {
        using var image = CreateImageWithIccProfile(1, 1, PixelFormat.Rgb24, [255, 255, 255], SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var profile = image.Metadata.GetIccColorProfile();
        Assert.NotNull(profile);
        Assert.Equal(IccColorSpace.Rgb, profile.DataColorSpace);
    }

    [Fact]
    public void ConvertToSrgb_NoProfile_ReturnsSameInstance()
    {
        using var image = Image.Create(2, 2, PixelFormat.Rgb24);
        var result = image.ConvertToSrgb();
        Assert.Same(image, result);
    }

    [Fact]
    public void ConvertToSrgb_MismatchedProfileChannelCount_ReturnsSameInstance()
    {
        // An Rgb24 (3-channel) image with a Gray (1-channel) ICC profile embedded is a nonsensical combination
        // -- ConvertToSrgb should treat this the same as "no usable profile" rather than misinterpreting bytes.
        using var image = CreateImageWithIccProfile(1, 1, PixelFormat.Rgb24, [128, 128, 128], SyntheticIccProfileBuilder.BuildGrayTrcProfile());
        var result = image.ConvertToSrgb();
        Assert.Same(image, result);
    }

    [Fact]
    public void ConvertToSrgb_RgbImageWithEmbeddedProfile_ProducesColorManagedRgba32()
    {
        using var whiteImage = CreateImageWithIccProfile(1, 1, PixelFormat.Rgb24, [255, 255, 255], SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        using var result = whiteImage.ConvertToSrgb();

        Assert.NotSame(whiteImage, result);
        Assert.Equal(PixelFormat.Rgba32, result.PixelFormat);
        Assert.False(result.HasAlpha);

        var pixel = result.GetPixelSpan();
        Assert.True(pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200);
        Assert.Equal(255, pixel[3]);
    }

    [Fact]
    public void ConvertToSrgb_MatchesLowLevelIccColorProfileConversion()
    {
        string path = Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "valid", "ycck.jpg");
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            Assert.Skip("External JPEG test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
        }

        using var stream = File.OpenRead(path);
        using var cmykImage = JpegDecoder.Decode(stream);
        var iccProfile = cmykImage.Metadata.GetIccColorProfile();
        Assert.NotNull(iccProfile);
        Assert.Equal(IccColorSpace.Cmyk, iccProfile.DataColorSpace);

        int pixelCount = cmykImage.Width * cmykImage.Height;
        var expected = new byte[pixelCount * 4];
        iccProfile.ConvertToSrgb(cmykImage.GetPixelSpan(), expected, pixelCount);

        using var result = cmykImage.ConvertToSrgb();
        Assert.Equal(PixelFormat.Rgba32, result.PixelFormat);
        Assert.True(expected.AsSpan().SequenceEqual(result.GetPixelSpan()));
    }
}
