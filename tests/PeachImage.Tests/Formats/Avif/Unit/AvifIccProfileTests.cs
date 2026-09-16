using PeachImage.Formats.Avif.Container;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Confirms an AVIF <c>colr</c> box's embedded ICC profile (<c>rICC</c> colour type) is captured into
/// <see cref="ImageMetadata"/> and exposed via <see cref="ImageMetadata.GetIccColorProfile"/> — this
/// already worked before the PeachPDF API-gap work (issue #1106's AVIF portion needed no PeachImage code
/// change), so this test exists to lock that behavior in as a regression guard. Exercises
/// <see cref="AvifContainerReader.Read"/> directly (container-level parsing only) rather than a full
/// <see cref="AvifDecoder.Decode"/>, since <see cref="AvifFixtureBuilder"/>'s dummy AV1 tile payloads
/// aren't valid enough for a real pixel decode — ICC capture happens entirely at the container level and
/// doesn't need one. Uses the same real, redistributable reference sRGB ICC profile (color.org's
/// <c>sRGB2014.icc</c>) already embedded in the test assembly for the TIFF ICC tests.
/// </summary>
public class AvifIccProfileTests
{
    [Fact]
    public void ColrBoxWithIccProfile_PopulatesMetadataAndGetIccColorProfileParsesIt()
    {
        byte[] iccBytes = LoadReferenceIccProfile();
        byte[] file = AvifFixtureBuilder.BuildSingleItem(8, 8, iccProfile: iccBytes);

        var metadata = new ImageMetadata();
        AvifContainerReader.Read(new MemoryStream(file), metadata);

        var profile = metadata.Profiles.Single(p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(iccBytes, profile.Data);

        var iccProfile = metadata.GetIccColorProfile();
        Assert.NotNull(iccProfile);
        Assert.Equal(IccColorSpace.Rgb, iccProfile.DataColorSpace);
        Assert.Equal(3, iccProfile.ChannelCount);
    }

    private static byte[] LoadReferenceIccProfile()
    {
        using var stream = typeof(AvifIccProfileTests).Assembly.GetManifestResourceStream("PeachImage.Tests.Formats.Tiff.Assets.reference-srgb.icc")
            ?? throw new InvalidOperationException("Embedded reference ICC profile resource not found.");
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }
}
