using System.Buffers.Binary;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit;

/// <summary>The PNG color chunks other than iCCP and cICP: gAMA, cHRM and sRGB, how they round trip, and what <see cref="PngDecoderOptions.ScreenGamma"/> does with them.</summary>
public class PngColorChunkTests
{
    private static readonly byte[] Gamma = [0x00, 0x00, 0xB1, 0x8F]; // 45455 = 1/2.2 in hundred-thousandths
    private static readonly byte[] Chromaticities = BuildChromaticities();

    private static byte[] BuildChromaticities()
    {
        // sRGB: white 0.3127,0.3290 red 0.64,0.33 green 0.30,0.60 blue 0.15,0.06, each in hundred-thousandths.
        int[] values = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];
        var bytes = new byte[32];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static Image Gray(byte value = 128)
    {
        var image = Image.Create(4, 4, PixelFormat.Gray8);
        image.GetPixelSpan().Fill(value);
        return image;
    }

    private static byte[] Encode(Image image, PngEncoderOptions? options = null)
    {
        using var ms = new MemoryStream();
        image.Save(ms, "png", options ?? new PngEncoderOptions());
        return ms.ToArray();
    }

    private static byte[]? DataOf(Image image, MetadataProfileKind kind) => image.Metadata.Profiles.FirstOrDefault(p => p.Kind == kind)?.Data;

    [Fact]
    public void GammaChromaticityAndSrgb_RoundTripAsMetadata()
    {
        using var source = Gray();
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Chromaticity, Data = Chromaticities });
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Srgb, Data = [0] });

        using var back = Image.Load(Encode(source));
        Assert.Equal(Gamma, DataOf(back, MetadataProfileKind.Gamma));
        Assert.Equal(Chromaticities, DataOf(back, MetadataProfileKind.Chromaticity));
        Assert.Equal(new byte[] { 0 }, DataOf(back, MetadataProfileKind.Srgb));
    }

    [Fact]
    public void ColorChunks_AreDroppedWithTheMetadata_ButTheIccProfileCanBeKept()
    {
        using var source = Gray();
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Srgb, Data = [0] });
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });

        using var none = Image.Load(Encode(source, new PngEncoderOptions { IncludeMetadata = false }));
        Assert.Null(DataOf(none, MetadataProfileKind.Gamma));
        Assert.Null(DataOf(none, MetadataProfileKind.Srgb));
        Assert.Null(none.Metadata.IccProfileBytes);

        using var iccOnly = Image.Load(Encode(source, new PngEncoderOptions { IncludeMetadata = false, IncludeIccProfile = true }));
        Assert.NotNull(iccOnly.Metadata.IccProfileBytes);
        Assert.Null(DataOf(iccOnly, MetadataProfileKind.Gamma));
    }

    [Fact]
    public void SrgbChunk_IsNotWrittenBesideAnIccProfile_ButStaysWhenTheProfileIsLeftOut()
    {
        using var source = Gray();
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Srgb, Data = [0] });
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });

        // PNG asks for at most one of sRGB and iCCP, so the profile wins and the sRGB claim is left out.
        using var both = Image.Load(Encode(source));
        Assert.NotNull(both.Metadata.IccProfileBytes);
        Assert.Null(DataOf(both, MetadataProfileKind.Srgb));

        // Without the profile (IncludeIccProfile = false) the sRGB chunk is still the file's only color claim and is kept.
        using var srgbOnly = Image.Load(Encode(source, new PngEncoderOptions { IncludeIccProfile = false }));
        Assert.Null(srgbOnly.Metadata.IccProfileBytes);
        Assert.Equal(new byte[] { 0 }, DataOf(srgbOnly, MetadataProfileKind.Srgb));

        // Gamma and chromaticities may accompany a profile (a decoder prefers the profile).
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        using var withGamma = Image.Load(Encode(source));
        Assert.Equal(Gamma, DataOf(withGamma, MetadataProfileKind.Gamma));
    }

    [Fact]
    public void ConvertedImages_NeverCarryStaleColorChunks()
    {
        using var source = Gray();
        using var rgb = source.ConvertTo(PixelFormat.Rgb24);
        rgb.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        rgb.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Srgb, Data = [0] });
        using var p3 = rgb.ConvertColorSpace(NamedColorSpace.DisplayP3);
        Assert.DoesNotContain(p3.Metadata.Profiles, p => p.Kind is MetadataProfileKind.Gamma or MetadataProfileKind.Srgb or MetadataProfileKind.Chromaticity);
        using var back = Image.Load(Encode(p3));
        Assert.Null(DataOf(back, MetadataProfileKind.Srgb));
    }

    [Fact]
    public void ScreenGamma_IsIgnoredWithoutAFileGamma_AndByDefault()
    {
        using var source = Gray(100);
        var bytes = Encode(source);
        using var plain = Image.Load(bytes);
        using var withScreen = Image.Load(new MemoryStream(bytes), new PngDecoderOptions { ScreenGamma = 2.2 });
        Assert.Equal(plain.GetPixelSpan().ToArray(), withScreen.GetPixelSpan().ToArray());

        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        var withGamma = Encode(source);
        using var untouched = Image.Load(withGamma);
        Assert.Equal(100, untouched.GetPixelSpan()[0]); // a gAMA chunk alone changes nothing
    }

    // output = round(255 * (input / 255) ^ (fileGamma / screenGamma)), the contract PngDecoderOptions.ScreenGamma states.
    private static int Corrected(int value, double fileGamma, double screenGamma) => (int)Math.Round(255 * Math.Pow(value / 255.0, fileGamma / screenGamma), MidpointRounding.AwayFromZero);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.2)]
    [InlineData(3.0)]
    public void ScreenGamma_CorrectsEightBitSamples_WhenTheFileDeclaresAGamma(double screenGamma)
    {
        using var source = Gray(100);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        var bytes = Encode(source);

        using var decoded = Image.Load(new MemoryStream(bytes), new PngDecoderOptions { ScreenGamma = screenGamma });
        Assert.Equal(Corrected(100, 0.45455, screenGamma), decoded.GetPixelSpan()[0]);
        Assert.NotEqual(100, decoded.GetPixelSpan()[0]);
    }

    [Fact]
    public void ScreenGamma_CorrectsSixteenBitSamples_ToTheSameContract()
    {
        using var source = Image.Create(2, 2, PixelFormat.Gray16);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan()).Fill(25000);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        using var decoded = Image.Load(new MemoryStream(Encode(source)), new PngDecoderOptions { ScreenGamma = 2.2 });
        int expected = (int)Math.Round(65535 * Math.Pow(25000 / 65535.0, 0.45455 / 2.2), MidpointRounding.AwayFromZero);
        Assert.Equal(expected, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(decoded.GetPixelSpan())[0]);
    }

    [Fact]
    public void SrgbChunk_ImpliesAFileGammaOfOneOverTwoPointTwo_ForScreenGamma()
    {
        using var source = Gray(100);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Srgb, Data = [0] });
        using var decoded = Image.Load(new MemoryStream(Encode(source)), new PngDecoderOptions { ScreenGamma = 1.0 });
        Assert.Equal(Corrected(100, 1 / 2.2, 1.0), decoded.GetPixelSpan()[0]);
    }

    [Fact]
    public void ScreenGamma_NeverTouchesAlpha()
    {
        using var source = Image.Create(2, 2, PixelFormat.Rgba32);
        source.GetPixelSpan().Fill(100);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = Gamma });
        using var corrected = Image.Load(new MemoryStream(Encode(source)), new PngDecoderOptions { ScreenGamma = 1.0 });
        Assert.NotEqual(100, corrected.GetPixelSpan()[0]);
        Assert.Equal(100, corrected.GetPixelSpan()[3]);
    }
}
