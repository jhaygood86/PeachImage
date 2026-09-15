using System.Reflection;
using PeachImage.Formats.Tiff;
using PeachImage.Tests.Formats.Tiff.Unit;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

/// <summary>
/// Tests TIFF's extraction of an embedded ICC profile (tag 34675) into <see cref="ImageMetadata.Profiles"/>.
/// The embedded profile bytes are a genuine, real-world ICC profile -- color.org's <c>sRGB2014.icc</c>
/// reference profile, published by the International Color Consortium under a license permitting
/// unrestricted copying/distribution/embedding -- not a hand-built synthetic one, since no fixture in the
/// TIFF corpus (checked byte-for-byte; none of its 154 files carry tag 34675) carries a real one.
/// </summary>
public class TiffIccProfileTests
{
    [Fact]
    public void Decode_WithEmbeddedIccProfile_PopulatesMetadataProfiles()
    {
        byte[] iccBytes = LoadReferenceIccProfile();
        using var image = DecodeBytes(BuildTiffWithIccProfile(iccBytes));

        var profile = Assert.Single(image.Metadata.Profiles);
        Assert.Equal(MetadataProfileKind.Icc, profile.Kind);
        Assert.Equal(iccBytes, profile.Data);
    }

    [Fact]
    public void Decode_WithEmbeddedIccProfile_GetIccColorProfileParsesIt()
    {
        byte[] iccBytes = LoadReferenceIccProfile();
        using var image = DecodeBytes(BuildTiffWithIccProfile(iccBytes));

        var iccProfile = image.Metadata.GetIccColorProfile();

        Assert.NotNull(iccProfile);
        Assert.Equal(IccColorSpace.Rgb, iccProfile.DataColorSpace);
        Assert.Equal(3, iccProfile.ChannelCount);
    }

    [Fact]
    public void Decode_WithoutIccProfile_HasNoIccMetadata()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            Strips = [[0]],
        };

        using var image = DecodeBytes(builder.Build());

        Assert.Empty(image.Metadata.Profiles);
        Assert.Null(image.Metadata.GetIccColorProfile());
    }

    [Fact]
    public void Decode_WithCorruptIccProfileTag_SkipsProfileWithoutThrowing()
    {
        // A declared count large enough to force offset-indirection, but with no corresponding external
        // data ever written -- TiffIfd.TryGetBytes must recognize the offset points past the end of the
        // file and return null rather than let TiffReader throw.
        var builder = new TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            Strips = [[0]],
            IccProfile = new byte[8], // Plausible size, but see below: bytes get corrupted post-build.
        };

        byte[] bytes = builder.Build();
        CorruptIccProfileOffset(bytes);

        using var image = DecodeBytes(bytes);

        Assert.Empty(image.Metadata.Profiles);
        Assert.Null(image.Metadata.GetIccColorProfile());
    }

    private static byte[] BuildTiffWithIccProfile(byte[] iccBytes)
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 2,
            BitsPerSample = 8,
            SamplesPerPixel = 3,
            Photometric = 2, // RGB
            Strips = [new byte[2 * 2 * 3]],
            IccProfile = iccBytes,
        };

        return builder.Build();
    }

    /// <summary>Finds the 34675 IFD entry in <paramref name="bytes"/> and rewrites its value-field offset to point past the end of the file.</summary>
    private static void CorruptIccProfileOffset(byte[] bytes)
    {
        ushort entryCount = BitConverter.ToUInt16(bytes, 8);
        int entryOffset = 10;
        for (int i = 0; i < entryCount; i++)
        {
            ushort tag = BitConverter.ToUInt16(bytes, entryOffset);
            if (tag == 34675)
            {
                uint hostileOffset = (uint)bytes.Length + 1_000_000;
                BitConverter.GetBytes(hostileOffset).CopyTo(bytes, entryOffset + 8);
                return;
            }

            entryOffset += 12;
        }

        throw new InvalidOperationException("Expected an ICC profile (tag 34675) entry in the built TIFF.");
    }

    private static byte[] LoadReferenceIccProfile()
    {
        using var stream = typeof(TiffIccProfileTests).Assembly.GetManifestResourceStream("PeachImage.Tests.Formats.Tiff.Assets.reference-srgb.icc")
            ?? throw new InvalidOperationException("Embedded reference ICC profile resource not found.");
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static Image DecodeBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return TiffDecoder.Decode(stream);
    }
}
