using PeachImage.Formats.Shared.Ccitt;

namespace PeachImage.Tests.Formats.Tiff.Unit;

public class CcittCodeTableTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunLengthCodes_ArePrefixFreeAndComplete(bool black)
    {
        var codes = CcittCodeTables.EnumerateCodes(black).ToArray();

        // 64 terminating + 27 make-up + 13 extended make-up codes.
        Assert.Equal(104, codes.Length);
        Assert.Equal(codes.Length, codes.Select(c => c.Run).Distinct().Count());

        for (int i = 0; i < codes.Length; i++)
        {
            for (int j = 0; j < codes.Length; j++)
            {
                if (i != j)
                {
                    Assert.False(codes[j].Code.StartsWith(codes[i].Code, StringComparison.Ordinal), $"{codes[i].Code} is a prefix of {codes[j].Code}");
                }
            }
        }
    }

    [Fact]
    public void RunLengthCodes_MatchKnownEntries()
    {
        var white = CcittCodeTables.EnumerateCodes(false).ToDictionary(c => c.Run, c => c.Code);
        var black = CcittCodeTables.EnumerateCodes(true).ToDictionary(c => c.Run, c => c.Code);
        Assert.Equal("00110101", white[0]);
        Assert.Equal("000111", white[1]);
        Assert.Equal("11011", white[64]);
        Assert.Equal("0000110111", black[0]);
        Assert.Equal("010", black[1]);
        Assert.Equal("0000001111", black[64]);
        Assert.Equal("00000001000", white[1792]);
        Assert.Equal("00000001000", black[1792]);
        Assert.Equal("000000011111", white[2560]);
    }
}
