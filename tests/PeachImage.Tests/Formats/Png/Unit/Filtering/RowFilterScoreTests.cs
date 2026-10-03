using PeachImage.Formats.Png.Filtering;
using Xunit;

namespace PeachImage.Tests.Formats.Png.Unit.Filtering;

public class RowFilterScoreTests
{
    private static long Reference(ReadOnlySpan<byte> filtered)
    {
        long sum = 0;
        foreach (byte b in filtered)
        {
            sum += b < 128 ? b : 256 - b;
        }

        return sum;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(255)]
    [InlineData(2048)]
    [InlineData(2049)]
    [InlineData(100_003)]
    public void Score_MatchesScalarDefinition(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        Assert.Equal(Reference(data), RowFilter.ScoreMinimumSumOfAbsoluteDifferences(data));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)127)]
    [InlineData((byte)128)]
    [InlineData((byte)129)]
    [InlineData((byte)255)]
    public void Score_HandlesEveryByteValue_AcrossTheAccumulatorFlushBoundary(byte value)
    {
        // Long constant runs of the worst-case byte (128 scores 128) stress the 16-bit accumulator flush.
        var data = new byte[16 * 128 * 3 + 5];
        Array.Fill(data, value);
        Assert.Equal(Reference(data), RowFilter.ScoreMinimumSumOfAbsoluteDifferences(data));
    }
}
