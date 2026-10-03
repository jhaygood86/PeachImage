using System.Buffers.Binary;
using PeachImage.Formats.Png;
using PeachImage.Formats.Png.Decoding;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Tests.Formats.Png.Unit.BitDepth;

public class PngBitUnpackerReferenceTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(1, 8)]
    [InlineData(1, 9)]
    [InlineData(1, 131)]
    [InlineData(2, 3)]
    [InlineData(2, 5)]
    [InlineData(2, 130)]
    [InlineData(4, 1)]
    [InlineData(4, 3)]
    [InlineData(4, 129)]
    [InlineData(8, 1)]
    [InlineData(8, 15)]
    [InlineData(8, 16)]
    [InlineData(8, 17)]
    [InlineData(8, 1000)]
    [InlineData(16, 1)]
    [InlineData(16, 33)]
    public void Unpack_MatchesPerSampleDefinition(int bitDepth, int sampleCount)
    {
        int rowBytes = ((sampleCount * bitDepth) + 7) / 8;
        var row = new byte[rowBytes];
        new Random(bitDepth * 1000 + sampleCount).NextBytes(row);

        var actual = new ushort[sampleCount];
        PngBitUnpacker.Unpack(row, bitDepth, sampleCount, samplesPerPixel: 1, actual);

        for (int i = 0; i < sampleCount; i++)
        {
            ushort expected;
            if (bitDepth == 16)
            {
                expected = BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(i * 2, 2));
            }
            else
            {
                int bitIndex = i * bitDepth;
                expected = (ushort)((row[bitIndex / 8] >> (8 - bitDepth - (bitIndex % 8))) & ((1 << bitDepth) - 1));
            }

            Assert.Equal(expected, actual[i]);
        }
    }
}

public class PngPaletteResolveTests
{
    private static readonly PngHeader Header = new(Width: 5, Height: 1, BitDepth: 8, ColorType: PngColorType.Palette, CompressionMethod: 0, FilterMethod: 0, InterlaceMethod: 0);

    private static PngPalette MakePalette(byte[]? alpha)
    {
        var palette = new PngPalette([10, 11, 12, 20, 21, 22, 30, 31, 32]);
        if (alpha is not null)
        {
            palette.SetAlpha(alpha);
        }

        return palette;
    }

    [Fact]
    public void Resolve_WithoutTransparency_WritesRgbTriples()
    {
        var dest = new byte[5 * 3];
        PngRowResolver.Resolve([0, 2, 1, 1, 0], 5, Header, MakePalette(null), null, [], false, dest);
        Assert.Equal(new byte[] { 10, 11, 12, 30, 31, 32, 20, 21, 22, 20, 21, 22, 10, 11, 12 }, dest);
    }

    [Fact]
    public void Resolve_WithShortTrns_WritesRgbaAndDefaultsMissingAlphaToOpaque()
    {
        // tRNS has entries for indices 0 and 1 only; index 2 must come out fully opaque.
        var dest = new byte[5 * 4];
        PngRowResolver.Resolve([0, 2, 1, 1, 0], 5, Header, MakePalette([0, 128]), null, [], false, dest);
        Assert.Equal(new byte[] { 10, 11, 12, 0, 30, 31, 32, 255, 20, 21, 22, 128, 20, 21, 22, 128, 10, 11, 12, 0 }, dest);
    }

    [Fact]
    public void Resolve_SetAlphaAfterFirstUse_RebuildsTheTable()
    {
        var palette = MakePalette(null);
        _ = palette.HasTransparency;
        palette.SetAlpha([7, 8, 9]);
        var dest = new byte[4];
        PngRowResolver.Resolve([2], 1, Header, palette, null, [], false, dest);
        Assert.Equal(new byte[] { 30, 31, 32, 9 }, dest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_IndexBeyondPalette_Throws(bool withTransparency)
    {
        var dest = new byte[2 * 4];
        var ex = Assert.Throws<PngDecodingException>(() =>
            PngRowResolver.Resolve([1, 3], 2, Header, MakePalette(withTransparency ? [255] : null), null, [], false, dest));
        Assert.Contains("Palette index 3", ex.Message);
    }
}
