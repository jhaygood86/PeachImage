using System.Text;
using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Tests.Metadata;

/// <summary>The "public copy" recipe: load, upright, resize, re-encode with no EXIF/XMP/IPTC but with the ICC profile.</summary>
public class PublicCopyTests
{
    private static readonly byte[] IccProfile = CreateIcc();

    private static byte[] CreateIcc()
    {
        var bytes = new byte[300];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    private static byte[] UploadWithEverything()
    {
        var exif = LocationFixtures.Exif(true, latitude: true, longitude: true, orientation: 6);
        return LocationFixtures.JpegWith(
            LocationFixtures.BaseJpeg(),
            (0xE1, exif),
            (0xE1, LocationFixtures.Concat(LocationFixtures.XmpHeader, LocationFixtures.XmpPacket(true))),
            (0xED, LocationFixtures.Photoshop(LocationFixtures.Iim((90, "San Francisco")))),
            (0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, .. IccProfile]));
    }

    private static EncoderOptions Strip(string format) => format switch
    {
        "jpeg" => new JpegEncoderOptions { Quality = 82, Subsampling = JpegChromaSubsampling.Yuv420, IncludeMetadata = false, IncludeIccProfile = true },
        "png" => new PeachImage.Formats.Png.PngEncoderOptions { IncludeMetadata = false, IncludeIccProfile = true },
        _ => new PeachImage.Formats.Webp.WebpEncoderOptions { IncludeMetadata = false, IncludeIccProfile = true },
    };

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void Recipe_DropsExifXmpAndIptc_AndKeepsTheIccProfile(string format)
    {
        var upload = UploadWithEverything();
        var allowed = new DecoderOptions { AllowedFormats = new HashSet<string> { "jpeg", "png", "gif", "webp" } };

        var info = Image.Identify(new MemoryStream(upload), allowed);
        Assert.True(info.HasLocation);
        Assert.Equal(ImageOrientation.Rotate90, info.Orientation);

        using var image = Image.Load(upload, allowed);
        image.ApplyOrientationInPlace(info.Orientation);
        using var small = image.Resize(4, 4, new ResizeOptions { Mode = ResizeMode.Max, CopyMetadata = true });

        using var output = new MemoryStream();
        small.Save(output, format, Strip(format));
        var bytes = output.ToArray();

        string text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("Exif", text);
        Assert.DoesNotContain("ns.adobe.com", text);
        Assert.DoesNotContain("GPSLatitude", text);
        Assert.DoesNotContain("Photoshop 3.0", text);
        Assert.DoesNotContain("8BIM", text);
        Assert.DoesNotContain("EXIF", text);
        Assert.DoesNotContain("XMP ", text);
        Assert.DoesNotContain("eXIf", text);
        Assert.DoesNotContain("iTXt", text);

        var reloadedInfo = Image.Identify(new MemoryStream(bytes));
        using var reloaded = Image.Load(bytes);
        Assert.False(reloadedInfo.HasLocation);
        Assert.False(reloaded.Metadata.HasLocation);
        Assert.DoesNotContain(reloaded.Metadata.Profiles, p => p.Kind is MetadataProfileKind.Exif or MetadataProfileKind.Xmp or MetadataProfileKind.Text);
        var icc = Assert.Single(reloaded.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(IccProfile, icc.Data);
    }

    [Fact]
    public void IccProfile_IsWrittenToJpegByDefault_AndDroppedOnRequest()
    {
        using var image = Image.Create(8, 8, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = IccProfile });

        using var defaults = new MemoryStream();
        image.Save(defaults, "jpeg", new JpegEncoderOptions());
        using var dropped = new MemoryStream();
        image.Save(dropped, "jpeg", new JpegEncoderOptions { IncludeIccProfile = false });
        using var large = new MemoryStream();
        var bigProfile = new byte[150_000];
        new Random(3).NextBytes(bigProfile);
        image.Metadata.Profiles[0] = new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = bigProfile };
        image.Save(large, "jpeg", new JpegEncoderOptions());

        using var withIcc = Image.Load(defaults.ToArray());
        using var withoutIcc = Image.Load(dropped.ToArray());
        using var withLargeIcc = Image.Load(large.ToArray());
        Assert.Equal(IccProfile, withIcc.Metadata.Profiles.Single(p => p.Kind == MetadataProfileKind.Icc).Data);
        Assert.DoesNotContain(withoutIcc.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(bigProfile, withLargeIcc.Metadata.Profiles.Single(p => p.Kind == MetadataProfileKind.Icc).Data);
    }

    [Fact]
    public void IncludeIccProfile_Null_KeepsTheLongStandingBehaviour()
    {
        using var image = Image.Create(8, 8, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = IccProfile });

        foreach (string format in new[] { "png", "webp" })
        {
            using var withMetadata = new MemoryStream();
            image.Save(withMetadata, format);
            using var withoutMetadata = new MemoryStream();
            image.Save(withoutMetadata, format, format == "png" ? new PeachImage.Formats.Png.PngEncoderOptions { IncludeMetadata = false } : new PeachImage.Formats.Webp.WebpEncoderOptions { IncludeMetadata = false });

            using var a = Image.Load(withMetadata.ToArray());
            using var b = Image.Load(withoutMetadata.ToArray());
            Assert.Contains(a.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
            Assert.DoesNotContain(b.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
        }
    }

    [Fact]
    public void ApplyOrientationInPlace_ResetsTheExifOrientation_WithoutMutatingTheOriginalProfile()
    {
        var exif = LocationFixtures.Exif(false, orientation: 6, latitude: true);
        using var image = Image.Create(6, 6, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        var original = exif.ToArray();

        image.ApplyOrientationInPlace(ImageOrientation.Rotate90);

        var after = Assert.Single(image.Metadata.Profiles).Data;
        Assert.Equal(ImageOrientation.Normal, ExifOrientationReader.Read(after));
        Assert.Equal(original, exif);
        Assert.True(image.Metadata.HasLocation);
    }

    [Fact]
    public void ApplyOrientation_Normal_LeavesMetadataAlone()
    {
        var exif = LocationFixtures.Exif(true, orientation: 6);
        using var image = Image.Create(6, 6, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });

        image.ApplyOrientationInPlace(ImageOrientation.Normal);

        Assert.Same(exif, image.Metadata.Profiles[0].Data);
    }

    [Fact]
    public void ResizeMax_ByDefaultReturnsTheSameInstance_AndAlwaysCopyReturnsAnIndependentOne()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb24);
        source.GetPixelSpan().Fill(9);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = IccProfile });

        var same = source.Resize(100, 100, new ResizeOptions { Mode = ResizeMode.Max });
        using var copy = source.Resize(100, 100, new ResizeOptions { Mode = ResizeMode.Max, AlwaysCopy = true });
        using var copyWithMetadata = source.Resize(100, 100, new ResizeOptions { Mode = ResizeMode.Max, AlwaysCopy = true, CopyMetadata = true });

        Assert.Same(source, same);
        Assert.NotSame(source, copy);
        Assert.NotSame(source, copyWithMetadata);
        Assert.Empty(copy.Metadata.Profiles);
        Assert.Single(copyWithMetadata.Metadata.Profiles);

        copy.Dispose();
        Assert.Equal(9, source.GetPixelSpan()[0]);
    }

    [Fact]
    public void ResizeWithoutCopyMetadata_StillDropsMetadata()
    {
        using var source = Image.Create(8, 8, PixelFormat.Rgb24);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = IccProfile });

        using var small = source.Resize(4, 4);

        Assert.Empty(small.Metadata.Profiles);
    }
}
