using PeachImage.Formats.Jbig2.Decoding;

namespace PeachImage.Tests.Formats.Jbig2.Unit;

public class Jbig2HuffmanTableTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void StandardTable_IsACompletePrefixCode(int number)
    {
        // Every table in T.88 Annex B uses exactly all of the code space.
        double kraft = Jbig2HuffmanTable.Standard(number).Describe().Where(line => line.PrefixLength > 0).Sum(line => Math.Pow(2, -line.PrefixLength));
        Assert.Equal(1.0, kraft, 12);
    }

    [Fact]
    public void StandardTables_MatchPrefixCodesPrintedInTheSpecification()
    {
        // Spot checks of codes printed in Annex B: table B.1, B.2 and B.10.
        var b1 = Jbig2HuffmanTable.Standard(1).Describe().ToArray();
        Assert.Equal((0, 1, 0u), b1[0]);
        Assert.Equal((16, 2, 2u), b1[1]);
        Assert.Equal((272, 3, 6u), b1[2]);
        Assert.Equal((65808, 3, 7u), b1[3]);

        var b10 = Jbig2HuffmanTable.Standard(10).Describe().ToArray();
        Assert.Equal((-21, 7, 0x7au), b10[0]);
        Assert.Equal((1094, 6, 0x3cu), b10[16]);
        Assert.Equal((4166, 8, 0xffu), b10[19]);
    }
}
