using System.Buffers.Binary;
using System.Text;

namespace PeachImage.Tests.Color;

public class ColorSpaceTests
{
    private static Image Float(float r, float g, float b, float a = 1f)
    {
        var image = Image.Create(1, 1, PixelFormat.RgbaF32);
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        s[0] = r;
        s[1] = g;
        s[2] = b;
        s[3] = a;
        return image;
    }

    private static float[] Pixel(Image image) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.GetPixelSpan()).ToArray();

    public static TheoryData<NamedColorSpace> ProfiledSpaces => new()
    {
        NamedColorSpace.Srgb, NamedColorSpace.LinearSrgb, NamedColorSpace.DisplayP3, NamedColorSpace.LinearDisplayP3, NamedColorSpace.Rec2020, NamedColorSpace.LinearRec2020,
    };

    [Theory]
    [MemberData(nameof(ProfiledSpaces))]
    public void BuiltInProfile_ParsesIsCompactAndIdentifiesItself(NamedColorSpace space)
    {
        var bytes = ColorProfiles.GetBytes(space);
        Assert.True(bytes.Length < 600, $"{space} profile is {bytes.Length} bytes");

        var profile = ColorProfiles.Get(space);
        Assert.Equal(IccColorSpace.Rgb, profile.DataColorSpace);
        Assert.Equal(space, profile.Identify());
        Assert.Same(profile, ColorProfiles.Get(space));
        Assert.True(IccColorProfile.TryCreate(bytes.Span, out var reparsed));
        Assert.Equal(space, reparsed!.Identify());
    }

    [Theory]
    [InlineData(NamedColorSpace.Rec2100Pq)]
    [InlineData(NamedColorSpace.Rec2100Hlg)]
    public void HdrSpaces_HaveNoIccProfileButHaveCicp(NamedColorSpace space)
    {
        Assert.False(ColorProfiles.HasProfile(space));
        Assert.Throws<NotSupportedException>(() => ColorProfiles.GetBytes(space));
        Assert.Equal(space, CicpCode.Of(space).ToNamedColorSpace());
        Assert.True(CicpCode.Of(space).IsHdr);
    }

    [Fact]
    public void Describe_ReportsPrimariesWhitePointAndTransfer()
    {
        var srgb = ColorProfiles.Get(NamedColorSpace.Srgb).Describe()!.Value;
        Assert.Equal(TransferFunction.Srgb, srgb.Transfer);
        Assert.Equal(0.64, srgb.Red.X, 3);
        Assert.Equal(0.33, srgb.Red.Y, 3);
        Assert.Equal(0.30, srgb.Green.X, 3);
        Assert.Equal(0.60, srgb.Green.Y, 3);
        Assert.Equal(0.15, srgb.Blue.X, 3);
        Assert.Equal(0.06, srgb.Blue.Y, 3);
        Assert.Equal(0.3127, srgb.White.X, 3);
        Assert.Equal(0.3290, srgb.White.Y, 3);

        var p3 = ColorProfiles.Get(NamedColorSpace.LinearDisplayP3).Describe()!.Value;
        Assert.Equal(TransferFunction.Linear, p3.Transfer);
        Assert.Equal(0.68, p3.Red.X, 3);
        Assert.Equal(0.69, p3.Green.Y, 3);
        Assert.Equal(NamedColorSpace.LinearDisplayP3, p3.Named);
    }

    [Fact]
    public void Describe_RecognizesAPurePowerLawAsGamma_AndNamesNothing()
    {
        var profile = new IccColorProfile(PowerLawProfile(2.2));
        var description = profile.Describe()!.Value;
        Assert.Equal(TransferFunction.Gamma, description.Transfer);
        Assert.Equal(2.2, description.Gamma, 2);
        Assert.Null(description.Named);
        Assert.Null(profile.Identify());
    }

    [Fact]
    public void Describe_IsNullForGrayProfiles()
    {
        Assert.Null(new IccColorProfile(PeachImage.Tests.Internal.Icc.SyntheticIccProfileBuilder.BuildGrayTrcProfile()).Describe());
    }

    [Fact]
    public void Srgb250_ConvertsToDisplayP3ReferenceValues()
    {
        // The reference triple from the CSS Color 4 conversion: sRGB (5, 250, 128) is color(display-p3 0.4504 0.9660 0.5524).
        using var source = Float(5 / 255f, 250 / 255f, 128 / 255f);
        using var converted = source.ConvertColorSpace(NamedColorSpace.DisplayP3);
        var p = Pixel(converted);
        Assert.Equal(0.4504, p[0], 3);
        Assert.Equal(0.9660, p[1], 3);
        Assert.Equal(0.5524, p[2], 3);
        Assert.Equal(1f, p[3]);
    }

    [Fact]
    public void Srgb8Bit_ConvertsToDisplayP3Bytes()
    {
        using var source = Image.Create(1, 1, PixelFormat.Rgb24);
        source.GetPixelSpan()[0] = 5;
        source.GetPixelSpan()[1] = 250;
        source.GetPixelSpan()[2] = 128;
        using var converted = source.ConvertColorSpace(NamedColorSpace.DisplayP3);
        Assert.Equal(PixelFormat.Rgb24, converted.PixelFormat);
        Assert.Equal((byte)Math.Round(0.4504 * 255), converted.GetPixelSpan()[0]);
        Assert.Equal((byte)Math.Round(0.9660 * 255), converted.GetPixelSpan()[1]);
        Assert.Equal((byte)Math.Round(0.5524 * 255), converted.GetPixelSpan()[2]);
    }

    [Fact]
    public void TransferFunctions_MatchKnownValues()
    {
        // sRGB 0.5 is 0.21404 linear; and linear 0.5 is 0.73536 sRGB.
        using var half = Float(0.5f, 0.5f, 0.5f);
        using var linear = half.ConvertColorSpace(NamedColorSpace.LinearSrgb);
        Assert.Equal(0.21404, Pixel(linear)[0], 4);

        using var back = linear.ConvertColorSpace(NamedColorSpace.Srgb);
        Assert.Equal(0.5, Pixel(back)[0], 5);

        using var lin = Float(0.5f, 0.5f, 0.5f);
        using var encoded = lin.ConvertColorSpace(NamedColorSpace.Srgb, new ColorConversionOptions { SourceColorSpace = NamedColorSpace.LinearSrgb });
        Assert.Equal(0.73536, Pixel(encoded)[0], 4);
    }

    [Theory]
    [InlineData(NamedColorSpace.DisplayP3)]
    [InlineData(NamedColorSpace.Rec2020)]
    [InlineData(NamedColorSpace.LinearRec2020)]
    public void RoundTrip_ThroughAnotherSpace_RestoresValuesWhenPreserved(NamedColorSpace through)
    {
        var options = new ColorConversionOptions { GamutHandling = GamutHandling.Preserve, TargetPixelFormat = PixelFormat.RgbaF32 };
        foreach (var (r, g, b) in new[] { (0.2f, 0.4f, 0.9f), (0.9f, 0.1f, 0.05f), (0f, 1f, 0f), (1f, 1f, 1f) })
        {
            using var source = Float(r, g, b);
            using var there = source.ConvertColorSpace(through, options);
            using var back = there.ConvertColorSpace(NamedColorSpace.Srgb, options);
            var p = Pixel(back);
            Assert.Equal(r, p[0], 4);
            Assert.Equal(g, p[1], 4);
            Assert.Equal(b, p[2], 4);
        }
    }

    [Fact]
    public void OutOfGamut_ClipsByDefault_AndPreservesOnRequest()
    {
        // Pure Display P3 green lies outside sRGB: its red coordinate goes negative.
        using var green = Float(0f, 1f, 0f);
        var asP3 = new ColorConversionOptions { SourceColorSpace = NamedColorSpace.DisplayP3 };

        using var clipped = green.ConvertColorSpace(NamedColorSpace.Srgb, asP3);
        Assert.Equal(0f, Pixel(clipped)[0]);

        asP3.GamutHandling = GamutHandling.Preserve;
        using var kept = green.ConvertColorSpace(NamedColorSpace.Srgb, asP3);
        Assert.True(Pixel(kept)[0] < -0.1f);
    }

    [Fact]
    public void Preserve_NeedsAFloatDestination()
    {
        using var image = Image.Create(2, 2, PixelFormat.Rgba32);
        var ex = Assert.Throws<ArgumentException>(() => image.ConvertColorSpace(NamedColorSpace.DisplayP3, new ColorConversionOptions { GamutHandling = GamutHandling.Preserve }));
        Assert.Contains("floating-point", ex.Message);
    }

    [Fact]
    public void Result_IsRetaggedWithTheTargetProfileAndCicp()
    {
        using var source = Image.Create(2, 2, PixelFormat.Rgba32);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.Rec2020).ToArray() });
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = [0, 0, 0, 1] });

        using var p3 = source.ConvertColorSpace(NamedColorSpace.DisplayP3);
        Assert.Equal(NamedColorSpace.DisplayP3, p3.Metadata.GetNamedColorSpace());
        Assert.Equal(CicpCode.Of(NamedColorSpace.DisplayP3), p3.Metadata.Cicp);
        Assert.DoesNotContain(p3.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Gamma);
        Assert.Equal(NamedColorSpace.Rec2020, source.Metadata.GetNamedColorSpace());

        using var srgb = source.ConvertColorSpace(NamedColorSpace.Srgb);
        Assert.Null(srgb.Metadata.IccProfileBytes);
        Assert.Equal(CicpCode.Of(NamedColorSpace.Srgb), srgb.Metadata.Cicp);
        Assert.Equal(NamedColorSpace.Srgb, srgb.Metadata.GetNamedColorSpace());
    }

    [Fact]
    public void EmbeddedProfile_IsTheSource_AndOverridesAreHonored()
    {
        using var image = Float(0f, 1f, 0f);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });

        using var viaEmbedded = image.ConvertColorSpace(NamedColorSpace.LinearSrgb, new ColorConversionOptions { GamutHandling = GamutHandling.Preserve });
        using var viaNamed = Float(0f, 1f, 0f).ConvertColorSpace(NamedColorSpace.LinearSrgb, new ColorConversionOptions { SourceColorSpace = NamedColorSpace.DisplayP3, GamutHandling = GamutHandling.Preserve });
        Assert.Equal(Pixel(viaNamed), Pixel(viaEmbedded));
        Assert.True(Pixel(viaEmbedded)[0] < 0f);

        using var overridden = image.ConvertColorSpace(NamedColorSpace.LinearSrgb, new ColorConversionOptions { SourceColorSpace = NamedColorSpace.Srgb });
        Assert.NotEqual(Pixel(viaEmbedded)[1], Pixel(overridden)[1]);
    }

    [Fact]
    public void Cicp_IsUsedWhenThereIsNoProfile()
    {
        using var image = Float(0f, 1f, 0f);
        image.Metadata.Cicp = CicpCode.Of(NamedColorSpace.DisplayP3);
        using var converted = image.ConvertColorSpace(NamedColorSpace.LinearSrgb, new ColorConversionOptions { GamutHandling = GamutHandling.Preserve });
        Assert.True(Pixel(converted)[0] < 0f);
    }

    [Fact]
    public void GrayImages_BecomeRgbOfTheSameDepth()
    {
        using var gray = Image.Create(1, 1, PixelFormat.Gray16);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(gray.GetPixelSpan())[0] = 40000;
        using var converted = gray.ConvertColorSpace(NamedColorSpace.DisplayP3);
        Assert.Equal(PixelFormat.Rgb48, converted.PixelFormat);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(converted.GetPixelSpan());
        Assert.Equal(samples[0], samples[1]);
        Assert.Equal(samples[1], samples[2]);
    }

    [Fact]
    public void AlphaIsNeverChanged_AndPremultipliedSourcesRoundTrip()
    {
        using var source = Image.Create(1, 1, PixelFormat.PremultipliedRgbaF32);
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        // Straight (0.8, 0.4, 0.2) at alpha 0.5, stored premultiplied.
        s[0] = 0.4f;
        s[1] = 0.2f;
        s[2] = 0.1f;
        s[3] = 0.5f;

        var options = new ColorConversionOptions { GamutHandling = GamutHandling.Preserve };
        using var p3 = source.ConvertColorSpace(NamedColorSpace.DisplayP3, options);
        Assert.Equal(PixelFormat.PremultipliedRgbaF32, p3.PixelFormat);
        Assert.Equal(0.5f, Pixel(p3)[3]);

        using var back = p3.ConvertColorSpace(NamedColorSpace.Srgb, options);
        var p = Pixel(back);
        Assert.Equal(0.4f, p[0], 4);
        Assert.Equal(0.2f, p[1], 4);
        Assert.Equal(0.1f, p[2], 4);
    }

    [Fact]
    public void CmykImages_AreRejected()
    {
        using var cmyk = Image.Create(1, 1, PixelFormat.Cmyk32);
        Assert.Throws<NotSupportedException>(() => cmyk.ConvertColorSpace(NamedColorSpace.Srgb));
    }

    [Fact]
    public void CustomProfileDestination_IsConverted()
    {
        var profile = new IccColorProfile(PowerLawProfile(2.2));
        using var source = Float(0.5f, 0.5f, 0.5f);
        using var converted = source.ConvertColorSpace(profile);
        // sRGB 0.5 -> linear 0.21404 -> 2.2 gamma encode = 0.21404^(1/2.2).
        Assert.Equal(Math.Pow(0.21404, 1 / 2.2), Pixel(converted)[0], 3);
        Assert.Equal(profile.ProfileBytes.ToArray(), converted.Metadata.IccProfileBytes);
    }

    [Fact]
    public void LutBasedSource_IsConvertedThroughTheIccEngine_AndLutDestinationIsRejected()
    {
        var lut = new IccColorProfile(LutProfile());

        // The LUT maps a linear-light RGB cube with sRGB primaries, so device (0.5, 0.5, 0.5) is linear 0.5.
        using var source = Float(0.5f, 0.5f, 0.5f);
        using var converted = source.ConvertColorSpace(NamedColorSpace.Srgb, new ColorConversionOptions { SourceProfile = lut });
        Assert.Equal(0.73536, Pixel(converted)[0], 2);

        var ex = Assert.Throws<IccProfileException>(() => source.ConvertColorSpace(lut));
        Assert.Contains("LUT-based", ex.Message);
    }

    private static byte[] PowerLawProfile(double gamma)
    {
        // Reuse the built-in sRGB colorants with a single-gamma curve.
        var bytes = ColorProfiles.GetBytes(NamedColorSpace.Srgb).ToArray();
        // The TRC tag data is the last 32-byte parametric curve; replace it with a 14-byte curv (padded to 16) and fix the tag sizes.
        int trcOffset = bytes.Length - 32;
        var result = new byte[bytes.Length - 16];
        Array.Copy(bytes, result, trcOffset);
        Encoding.ASCII.GetBytes("curv", result.AsSpan(trcOffset, 4));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(trcOffset + 8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(trcOffset + 12), (ushort)Math.Round(gamma * 256));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0), (uint)result.Length);
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(128));
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + (i * 12);
            string signature = Encoding.ASCII.GetString(result, entry, 4);
            if (signature is "rTRC" or "gTRC" or "bTRC")
            {
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 8), 14);
            }
        }

        return result;
    }

    // A v2 RGB device profile whose A2B0 is a 2-point-per-axis lut16 mapping a linear RGB cube to D50 XYZ with sRGB primaries.
    private static byte[] LutProfile()
    {
        var matrix = PeachImage.Internal.Color.NamedSpaces.ToXyzD50(NamedColorSpace.Srgb).ToArray();
        var lut = new byte[52 + (3 * 2 * 2) + (8 * 3 * 2) + (3 * 2 * 2)];
        Encoding.ASCII.GetBytes("mft2", lut.AsSpan(0, 4));
        lut[8] = 3;
        lut[9] = 3;
        lut[10] = 2;
        for (int i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(lut.AsSpan(12 + (i * 4)), i % 4 == 0 ? 0x10000 : 0);
        }

        BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(50), 2);
        int p = 52;
        for (int c = 0; c < 3; c++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p), 0);
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p + 2), 65535);
            p += 4;
        }

        for (int index = 0; index < 8; index++)
        {
            // The first input channel varies slowest.
            double r = (index >> 2) & 1, g = (index >> 1) & 1, b = index & 1;
            for (int channel = 0; channel < 3; channel++)
            {
                double value = (matrix[(channel * 3)] * r) + (matrix[(channel * 3) + 1] * g) + (matrix[(channel * 3) + 2] * b);
                BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p), (ushort)Math.Round(value * 32768.0));
                p += 2;
            }
        }

        for (int c = 0; c < 3; c++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p), 0);
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p + 2), 65535);
            p += 4;
        }

        var white = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ", white.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(white.AsSpan(8), (int)Math.Round(0.9642 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(white.AsSpan(12), 65536);
        BinaryPrimitives.WriteInt32BigEndian(white.AsSpan(16), (int)Math.Round(0.8249 * 65536));

        var tags = new (string Signature, byte[] Data)[] { ("A2B0", lut), ("wtpt", white) };
        int tableSize = 4 + (tags.Length * 12);
        int cursor = 128 + tableSize;
        var buffer = new byte[cursor + tags.Sum(t => t.Data.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)buffer.Length);
        Encoding.ASCII.GetBytes("mntr", buffer.AsSpan(12, 4));
        Encoding.ASCII.GetBytes("RGB ", buffer.AsSpan(16, 4));
        Encoding.ASCII.GetBytes("XYZ ", buffer.AsSpan(20, 4));
        Encoding.ASCII.GetBytes("acsp", buffer.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(128), (uint)tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            int entry = 132 + (i * 12);
            Encoding.ASCII.GetBytes(tags[i].Signature, buffer.AsSpan(entry, 4));
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(entry + 4), (uint)cursor);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(entry + 8), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(buffer, cursor);
            cursor += tags[i].Data.Length;
        }

        return buffer;
    }
}
