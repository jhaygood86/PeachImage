using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.Unit;

public class AvifGridTests
{
    [Fact]
    public void Identify_Grid_UsesGridDescriptorOutputSize_WhenNoItemIspe()
    {
        byte[] file = AvifFixtureBuilder.BuildGrid(rows: 1, columns: 2, outputWidth: 100, outputHeight: 48);

        using var stream = new MemoryStream(file);
        var info = Image.Identify(stream);

        Assert.Equal(100, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(PixelFormat.Rgb24, info.PixelFormat);
    }

    [Fact]
    public void Identify_Grid_PrefersItemIspe_WhenPresent()
    {
        byte[] file = AvifFixtureBuilder.BuildGrid(rows: 2, columns: 2, outputWidth: 64, outputHeight: 64, includeItemIspe: true);

        using var stream = new MemoryStream(file);
        var info = Image.Identify(stream);

        Assert.Equal(64, info.Width);
        Assert.Equal(64, info.Height);
    }

    [Fact]
    public void Identify_Grid_MismatchedTileCount_ThrowsDecodingException()
    {
        // 2x2 grid declares 4 tiles but the fixture only wires up dimg references for exactly rows*columns,
        // so instead exercise the mismatch by asking for a 1x1 grid geometry read from data sized for 2.
        byte[] file = AvifFixtureBuilder.BuildGrid(rows: 1, columns: 2, outputWidth: 10, outputHeight: 10);

        // Corrupt the grid descriptor's rows_minus_one byte (at a known offset within mdat) to claim 3 rows
        // instead of 1, without changing the dimg reference list -- simulates a file where the declared grid
        // geometry disagrees with the actual tile reference count.
        int gridDescriptorOffset = FindGridDescriptorOffset(file);
        file[gridDescriptorOffset + 2] = 2; // rows_minus_one = 2 -> 3 rows

        using var stream = new MemoryStream(file);
        Assert.Throws<AvifDecodingException>(() => Image.Identify(stream));
    }

    private static int FindGridDescriptorOffset(byte[] file)
    {
        // The grid descriptor is the first 8 bytes of mdat's payload for AvifFixtureBuilder.BuildGrid (item 1
        // is always the grid item, listed first). mdat is the last box in the file.
        for (int i = file.Length - 1; i >= 8; i--)
        {
            if (file[i - 4] == (byte)'m' && file[i - 3] == (byte)'d' && file[i - 2] == (byte)'a' && file[i - 1] == (byte)'t')
            {
                return i; // payload starts right after the 4-byte FourCC
            }
        }

        throw new InvalidOperationException("mdat box not found in fixture.");
    }
}
