using PeachImage.Formats.Png.Filtering;

namespace PeachImage.Tests.Formats.Png.Unit.Filtering;

public class RowFilterTests
{
    [Theory]
    [InlineData((byte)PngFilterType.None)]
    [InlineData((byte)PngFilterType.Sub)]
    [InlineData((byte)PngFilterType.Up)]
    [InlineData((byte)PngFilterType.Average)]
    [InlineData((byte)PngFilterType.Paeth)]
    public void Filter_ThenUnfilter_RoundTrips_FirstRow(byte filterTypeByte)
    {
        var filterType = (PngFilterType)filterTypeByte;
        byte[] raw = [10, 20, 30, 40, 50, 60, 70, 80, 90];
        const int bpp = 3;

        byte[] filtered = new byte[raw.Length];
        RowFilter.Filter(raw, ReadOnlySpan<byte>.Empty, filterType, bpp, filtered);

        byte[] reconstructed = (byte[])filtered.Clone();
        RowFilter.Unfilter(reconstructed, ReadOnlySpan<byte>.Empty, filterType, bpp);

        Assert.Equal(raw, reconstructed);
    }

    [Theory]
    [InlineData((byte)PngFilterType.None)]
    [InlineData((byte)PngFilterType.Sub)]
    [InlineData((byte)PngFilterType.Up)]
    [InlineData((byte)PngFilterType.Average)]
    [InlineData((byte)PngFilterType.Paeth)]
    public void Filter_ThenUnfilter_RoundTrips_WithPreviousRow(byte filterTypeByte)
    {
        var filterType = (PngFilterType)filterTypeByte;
        byte[] previousRaw = [5, 250, 128, 0, 255, 64];
        byte[] raw = [10, 20, 200, 199, 1, 254];
        const int bpp = 2;

        byte[] filtered = new byte[raw.Length];
        RowFilter.Filter(raw, previousRaw, filterType, bpp, filtered);

        byte[] reconstructed = (byte[])filtered.Clone();
        RowFilter.Unfilter(reconstructed, previousRaw, filterType, bpp);

        Assert.Equal(raw, reconstructed);
    }

    [Fact]
    public void Unfilter_None_LeavesRowUnchanged()
    {
        byte[] row = [1, 2, 3, 4];
        byte[] original = (byte[])row.Clone();

        RowFilter.Unfilter(row, ReadOnlySpan<byte>.Empty, PngFilterType.None, bpp: 1);

        Assert.Equal(original, row);
    }

    [Fact]
    public void Unfilter_Sub_AddsPreviousPixelInSameRow()
    {
        // bpp=1: recon[x] = filt[x] + recon[x-1] (recon[-1] treated as 0).
        byte[] row = [10, 5, 5, 5];

        RowFilter.Unfilter(row, ReadOnlySpan<byte>.Empty, PngFilterType.Sub, bpp: 1);

        Assert.Equal([10, 15, 20, 25], row);
    }

    [Fact]
    public void Unfilter_Up_AddsSameColumnFromPreviousRow()
    {
        byte[] previousRow = [100, 150, 200];
        byte[] row = [1, 2, 3];

        RowFilter.Unfilter(row, previousRow, PngFilterType.Up, bpp: 1);

        Assert.Equal([101, 152, 203], row);
    }

    [Fact]
    public void PaethPredictor_TieBreaksTowardA()
    {
        // a=b=c=0 -> p=0, pa=pb=pc=0 -> a wins by the <= tie rule.
        byte result = PaethPredictor.Predict(a: 0, b: 0, c: 0);
        Assert.Equal(0, result);

        // a=10,b=10,c=10 -> p=10, all distances 0 -> a (10) wins.
        Assert.Equal(10, PaethPredictor.Predict(10, 10, 10));
    }
}
