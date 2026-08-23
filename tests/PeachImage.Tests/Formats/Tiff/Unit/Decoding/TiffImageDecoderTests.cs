using PeachImage.Formats.Tiff;
using PeachImage.Formats.Tiff.Decoding;
using PeachImage.Tests.Formats.Tiff.Unit;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

public class TiffImageDecoderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decode_UncompressedGrayscale8Bit_BothByteOrders_MatchesSourcePixels(bool littleEndian)
    {
        byte[] pixels = [0, 10, 20, 30, 40, 50, 60, 70, 80];
        var builder = new TiffFixtureBuilder
        {
            Width = 3,
            Height = 3,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1, // BlackIsZero
            LittleEndian = littleEndian,
            Strips = [pixels],
        };

        using var image = DecodeBytes(builder.Build());

        Assert.Equal(3, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(PixelFormat.Gray8, image.PixelFormat);
        Assert.Equal(pixels, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_WhiteIsZero_InvertsGrayscaleSamples()
    {
        byte[] pixels = [0, 64, 128, 255];
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 2,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 0, // WhiteIsZero
            Strips = [pixels],
        };

        using var image = DecodeBytes(builder.Build());

        byte[] expected = [255, 191, 127, 0];
        Assert.Equal(expected, image.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Decode_SubByteGrayscale_ScalesToFullDisplayRange(int bitDepth)
    {
        // One packed byte holding every possible sample value for this bit depth, MSB-first, then padded
        // with zero bits to fill the byte -- exercises TiffBitUnpacker's shift/mask path end to end.
        int maxValue = (1 << bitDepth) - 1;
        int samplesPerByte = 8 / bitDepth;
        var packedRow = new byte[(maxValue + samplesPerByte) / samplesPerByte];

        for (int i = 0; i <= maxValue; i++)
        {
            int byteIndex = i / samplesPerByte;
            int shift = 8 - bitDepth - ((i % samplesPerByte) * bitDepth);
            packedRow[byteIndex] |= (byte)(i << shift);
        }

        var builder = new TiffFixtureBuilder
        {
            Width = maxValue + 1,
            Height = 1,
            BitsPerSample = bitDepth,
            SamplesPerPixel = 1,
            Photometric = 1,
            Strips = [packedRow],
        };

        using var image = DecodeBytes(builder.Build());

        var pixels = image.GetPixelSpan();
        for (int i = 0; i <= maxValue; i++)
        {
            int expected = (int)Math.Round(i * 255.0 / maxValue);
            Assert.Equal((byte)expected, pixels[i]);
        }
    }

    [Fact]
    public void Decode_Rgb24_NoAlpha_MatchesSourcePixels()
    {
        byte[] pixels =
        [
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            10, 20, 30,
        ];
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 2,
            BitsPerSample = 8,
            SamplesPerPixel = 3,
            Photometric = 2, // RGB
            Strips = [pixels],
        };

        using var image = DecodeBytes(builder.Build());

        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
        Assert.Equal(pixels, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_Rgba32_StraightAlpha_MatchesSourcePixels()
    {
        byte[] pixels =
        [
            255, 0, 0, 128,
            0, 255, 0, 64,
        ];
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 4,
            Photometric = 2,
            ExtraSamples = [2], // unassociated (straight) alpha
            Strips = [pixels],
        };

        using var image = DecodeBytes(builder.Build());

        Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
        Assert.Equal(pixels, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_Rgba32_PremultipliedAlpha_UnPremultipliesExactly()
    {
        // Premultiplied at alpha=128 (~50%): straight red 255 -> premultiplied ~128 -> un-premultiply back
        // toward 255 (255*128/128, exact since alpha divides evenly here).
        byte[] pixels = [128, 0, 0, 128];
        var builder = new TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 4,
            Photometric = 2,
            ExtraSamples = [1], // associated (premultiplied) alpha
            Strips = [pixels],
        };

        using var image = DecodeBytes(builder.Build());

        var row = image.GetPixelSpan();
        Assert.Equal(255, row[0]);
        Assert.Equal(0, row[1]);
        Assert.Equal(0, row[2]);
        Assert.Equal(128, row[3]);
    }

    [Fact]
    public void Decode_Palette_ResolvesThroughColorMap()
    {
        // 2-bit palette, 4 entries: black, red, green, blue (ColorMap entries are 16-bit-scale).
        ushort[] colorMap =
        [
            0, 65535, 0, 0, // R: entry0=0, entry1=65535, entry2=0, entry3=0
            0, 0, 65535, 0, // G
            0, 0, 0, 65535, // B
        ];
        byte[] packedIndices = [0b_00_01_10_11]; // indices 0,1,2,3 for a 4-pixel row
        var builder = new TiffFixtureBuilder
        {
            Width = 4,
            Height = 1,
            BitsPerSample = 2,
            SamplesPerPixel = 1,
            Photometric = 3, // Palette
            ColorMap = colorMap,
            Strips = [packedIndices],
        };

        using var image = DecodeBytes(builder.Build());

        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
        byte[] expected = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255];
        Assert.Equal(expected, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_Cmyk_MatchesSourcePixels()
    {
        byte[] pixels = [0, 0, 0, 255, 128, 64, 32, 0];
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 4,
            Photometric = 5, // Separated/CMYK
            Strips = [pixels],
        };

        using var image = DecodeBytes(builder.Build());

        Assert.Equal(PixelFormat.Cmyk32, image.PixelFormat);
        Assert.Equal(pixels, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_PackBits_DecompressesCorrectly()
    {
        // Row 1 (literal run of 4): "ABCD". Row 2 (repeat run): 4x 'Z'.
        byte[] compressed = [3, (byte)'A', (byte)'B', (byte)'C', (byte)'D', unchecked((byte)-3), (byte)'Z'];
        var builder = new TiffFixtureBuilder
        {
            Width = 4,
            Height = 2,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            Compression = 32773,
            RowsPerStrip = 2,
            Strips = [compressed],
        };

        using var image = DecodeBytes(builder.Build());

        byte[] expected = [(byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'Z', (byte)'Z', (byte)'Z', (byte)'Z'];
        Assert.Equal(expected, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_Predictor2_UndoesHorizontalDifferencing()
    {
        // Original row: 10, 20, 15, 40. Differenced (Predictor=2): 10, 10, -5(=251 wrapped), 25.
        byte[] differenced = [10, 10, 251, 25];
        var builder = new TiffFixtureBuilder
        {
            Width = 4,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            Predictor = 2,
            Strips = [differenced],
        };

        using var image = DecodeBytes(builder.Build());

        byte[] expected = [10, 20, 15, 40];
        Assert.Equal(expected, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_RowsPerStripAbsent_TreatsWholeImageAsOneStrip()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6];
        var builder = new TiffFixtureBuilder
        {
            Width = 3,
            Height = 2,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            RowsPerStrip = null,
            Strips = [pixels],
        };

        using var image = DecodeBytes(builder.Build());

        Assert.Equal(pixels, image.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Decode_MultiStrip_ReconstructsRowsInOrder()
    {
        byte[] strip0 = [1, 2, 3];
        byte[] strip1 = [4, 5, 6];
        byte[] strip2 = [7, 8, 9];
        var builder = new TiffFixtureBuilder
        {
            Width = 3,
            Height = 3,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            RowsPerStrip = 1,
            Strips = [strip0, strip1, strip2],
        };

        using var image = DecodeBytes(builder.Build());

        byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        Assert.Equal(expected, image.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(6)] // Old-style JPEG.
    [InlineData(8)] // Deflate.
    public void Decode_UnsupportedCompression_ThrowsUnsupportedFeature(int compression)
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            OverrideCompressionValue = (uint)compression,
            Strips = [new byte[1]],
        };

        Assert.Throws<TiffUnsupportedFeatureException>(() => DecodeBytes(builder.Build()));
    }

    [Fact]
    public void Decode_TiledOrganization_ThrowsUnsupportedFeature()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 16,
            Height = 16,
            WriteTileTagInstead = true,
            Strips = [new byte[256]],
        };

        Assert.Throws<TiffUnsupportedFeatureException>(() => DecodeBytes(builder.Build()));
    }

    [Fact]
    public void Decode_PlanarConfiguration2_ThrowsUnsupportedFeature()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 2,
            Height = 1,
            SamplesPerPixel = 3,
            Photometric = 2,
            PlanarConfiguration = 2,
            Strips = [new byte[6]],
        };

        Assert.Throws<TiffUnsupportedFeatureException>(() => DecodeBytes(builder.Build()));
    }

    [Fact]
    public void Decode_UnsupportedBitDepth_ThrowsUnsupportedFeature()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            BitsPerSample = 3,
            Strips = [new byte[1]],
        };

        Assert.Throws<TiffUnsupportedFeatureException>(() => DecodeBytes(builder.Build()));
    }

    [Fact]
    public void Decode_UnsupportedPhotometric_ThrowsUnsupportedFeature()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            SamplesPerPixel = 3,
            OverridePhotometricValue = 6, // YCbCr
            Strips = [new byte[3]],
        };

        Assert.Throws<TiffUnsupportedFeatureException>(() => DecodeBytes(builder.Build()));
    }

    [Fact]
    public void Decode_MissingStripOffsets_ThrowsDecodingException()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            OmitStripOffsets = true,
            Strips = [new byte[1]],
        };

        Assert.Throws<TiffDecodingException>(() => DecodeBytes(builder.Build()));
    }

    [Fact]
    public void Decode_BigTiffMagic_ThrowsUnsupportedFeature()
    {
        byte[] bytes = [(byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0];

        Assert.Throws<TiffUnsupportedFeatureException>(() => DecodeBytes(bytes));
    }

    [Fact]
    public void Decode_TruncatedHeader_ThrowsDecodingException()
    {
        byte[] bytes = [(byte)'I', (byte)'I', 42, 0];

        Assert.Throws<TiffDecodingException>(() => DecodeBytes(bytes));
    }

    [Fact]
    public void Identify_ReturnsSameDimensionsAndFormatAsDecode()
    {
        var builder = new TiffFixtureBuilder
        {
            Width = 5,
            Height = 7,
            SamplesPerPixel = 3,
            Photometric = 2,
            Strips = [new byte[5 * 7 * 3]],
        };

        byte[] bytes = builder.Build();
        using var stream1 = new MemoryStream(bytes);
        var info = TiffDecoder.Identify(stream1);

        using var stream2 = new MemoryStream(bytes);
        using var image = TiffDecoder.Decode(stream2);

        Assert.Equal(image.Width, info.Width);
        Assert.Equal(image.Height, info.Height);
        Assert.Equal(image.PixelFormat, info.PixelFormat);
        Assert.Equal("tiff", info.FormatName);
    }

    private static Image DecodeBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return TiffDecoder.Decode(stream);
    }
}
