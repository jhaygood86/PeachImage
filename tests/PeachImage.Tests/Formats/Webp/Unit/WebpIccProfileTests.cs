using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.Unit;

/// <summary>
/// Confirms an embedded ICC profile survives a WebP round trip and is exposed via
/// <see cref="ImageMetadata.GetIccColorProfile"/> — this already worked before the PeachPDF API-gap work
/// (issue #1106's WebP portion needed no PeachImage code change), so this test exists to lock that
/// behavior in as a regression guard. Uses the same real, redistributable reference sRGB ICC profile
/// (color.org's <c>sRGB2014.icc</c>) already embedded in the test assembly for the TIFF ICC tests.
/// </summary>
public class WebpIccProfileTests
{
    [Fact]
    public void EmbeddedIccProfile_RoundTrips_AndGetIccColorProfileParsesIt()
    {
        byte[] iccBytes = LoadReferenceIccProfile();
        var source = Image.Create(4, 4, PixelFormat.Rgb24);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = iccBytes });

        using var ms = new MemoryStream();
        source.Save(ms, "webp", new WebpEncoderOptions());
        ms.Position = 0;

        var decoded = Image.Load(ms);

        var profile = decoded.Metadata.Profiles.Single(p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(iccBytes, profile.Data);

        var iccProfile = decoded.Metadata.GetIccColorProfile();
        Assert.NotNull(iccProfile);
        Assert.Equal(IccColorSpace.Rgb, iccProfile.DataColorSpace);
        Assert.Equal(3, iccProfile.ChannelCount);
    }

    private static byte[] LoadReferenceIccProfile()
    {
        using var stream = typeof(WebpIccProfileTests).Assembly.GetManifestResourceStream("PeachImage.Tests.Formats.Tiff.Assets.reference-srgb.icc")
            ?? throw new InvalidOperationException("Embedded reference ICC profile resource not found.");
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }
}
