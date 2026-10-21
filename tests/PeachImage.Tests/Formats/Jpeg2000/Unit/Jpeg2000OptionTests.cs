using PeachImage.Formats.Jpeg2000;

namespace PeachImage.Tests.Formats.Jpeg2000.Unit;

/// <summary>Colour-space handling, as a PDF <c>JPXDecode</c> consumer would exercise it.</summary>
public class Jpeg2000OptionTests
{
    private const string Prefix = "PeachImage.Tests.Formats.Jpeg2000.Assets.";

    [Fact]
    public void IgnoreColorSpace_SkipsSyccConversion()
    {
        using var converted = Image.Load(ReadAsset("box_sycc.jp2"));
        using var raw = Image.Load(ReadAsset("box_sycc.jp2"), new Jpeg2000DecoderOptions { IgnoreColorSpace = true });
        using var srgb = Image.Load(ReadAsset("box_srgb.jp2"));

        Assert.False(Same(converted, srgb));
        Assert.True(Same(raw, srgb)); // The same codestream, with the components left exactly as stored.
    }

    [Fact]
    public void IgnoreColorSpace_SkipsPaletteExpansion()
    {
        using var expanded = Image.Load(ReadAsset("box_palette.jp2"));
        using var indices = Image.Load(ReadAsset("box_palette.jp2"), new Jpeg2000DecoderOptions { IgnoreColorSpace = true });
        Assert.Equal(PixelFormat.Rgb24, expanded.PixelFormat);
        Assert.Equal(PixelFormat.Gray8, indices.PixelFormat);
    }

    [Fact]
    public void IgnoreColorSpace_FourComponentsAreCmyk()
    {
        using var image = Image.Load(ReadAsset("four_components.j2k"), new Jpeg2000DecoderOptions { IgnoreColorSpace = true });
        Assert.Equal(PixelFormat.Cmyk32, image.PixelFormat);
        Assert.False(image.HasAlpha);

        using var rgba = Image.Load(ReadAsset("four_components.j2k"));
        Assert.Equal(PixelFormat.Rgba32, rgba.PixelFormat);
        Assert.True(rgba.HasAlpha);
    }

    [Fact]
    public void IccProfileBox_IsAttachedAsMetadata()
    {
        byte[] icc = ReadTiffAsset("reference-srgb.icc");
        byte[] codestream = ReadAsset("rgb_raw_codestream.j2k");
        byte[] file = Jp2(codestream, icc);

        using var image = Image.Load(file);
        var profile = Assert.Single(image.Metadata.Profiles);
        Assert.Equal(MetadataProfileKind.Icc, profile.Kind);
        Assert.Equal(icc, profile.Data);
        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);

        using var ignoring = Image.Load(file, new Jpeg2000DecoderOptions { IgnoreColorSpace = true });
        Assert.Empty(ignoring.Metadata.Profiles);
    }

    [Fact]
    public void ReducedResolution_MatchesAHalfSizeDecode()
    {
        using var half = Image.Load(ReadAsset("rgb_lossless.jp2"), new Jpeg2000DecoderOptions { ReduceResolutionLevels = 1 });
        using var quarter = Image.Load(ReadAsset("rgb_lossless.jp2"), new Jpeg2000DecoderOptions { ReduceResolutionLevels = 2 });
        Assert.Equal(75, half.Width);
        Assert.Equal(55, half.Height);
        Assert.Equal(38, quarter.Width);
        Assert.Equal(28, quarter.Height);

        var info = Image.Identify(new MemoryStream(ReadAsset("rgb_lossless.jp2")), new Jpeg2000DecoderOptions { ReduceResolutionLevels = 1 });
        Assert.Equal(75, info.Width);
    }

    [Theory]
    [InlineData("rgb_levels1.jp2")]
    [InlineData("rgb_tiles.jp2")]
    [InlineData("image_offset_tiles.j2k")]
    public void ReducedResolution_IsClampedAndDecodes(string file)
    {
        using var image = Image.Load(ReadAsset(file), new Jpeg2000DecoderOptions { ReduceResolutionLevels = 9 });
        Assert.True(image.Width >= 1 && image.Height >= 1);
    }

    private static bool Same(Image a, Image b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.PixelFormat != b.PixelFormat)
        {
            return false;
        }

        for (int y = 0; y < a.Height; y++)
        {
            if (!a.GetRowSpan(y).SequenceEqual(b.GetRowSpan(y)))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Jp2(byte[] codestream, byte[] icc)
    {
        static byte[] Box(string type, byte[] payload)
        {
            var box = new byte[8 + payload.Length];
            box[0] = (byte)(box.Length >> 24);
            box[1] = (byte)(box.Length >> 16);
            box[2] = (byte)(box.Length >> 8);
            box[3] = (byte)box.Length;
            for (int i = 0; i < 4; i++)
            {
                box[4 + i] = (byte)type[i];
            }

            payload.CopyTo(box, 8);
            return box;
        }

        byte[] ihdr = [0, 0, 0, 110, 0, 0, 0, 150, 0, 3, 7, 7, 0, 0];
        // ihdr stores height then width; the sample file is 150x110.
        ihdr = [0, 0, 0, 110, 0, 0, 0, 150, 0, 3, 7, 7, 0, 0];
        byte[] colr = [.. new byte[] { 2, 0, 0 }, .. icc];
        byte[] jp2h = [.. Box("ihdr", ihdr), .. Box("colr", colr)];
        return
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", [(byte)'j', (byte)'p', (byte)'2', (byte)' ', 0, 0, 0, 0, (byte)'j', (byte)'p', (byte)'2', (byte)' ']),
            .. Box("jp2h", jp2h),
            .. Box("jp2c", codestream),
        ];
    }

    private static byte[] ReadAsset(string name) => ReadResource(Prefix + name);

    private static byte[] ReadTiffAsset(string name) => ReadResource("PeachImage.Tests.Formats.Tiff.Assets." + name);

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(Jpeg2000OptionTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
