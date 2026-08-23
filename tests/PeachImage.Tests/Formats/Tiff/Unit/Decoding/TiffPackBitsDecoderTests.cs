using PeachImage.Formats.Tiff.Decoding;

namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

public class TiffPackBitsDecoderTests
{
    [Fact]
    public void Decode_LiteralRun_CopiesBytesVerbatim()
    {
        byte[] compressed = [3, 1, 2, 3, 4];
        var output = new byte[4];

        TiffPackBitsDecoder.Decode(compressed, output);

        Assert.Equal([1, 2, 3, 4], output);
    }

    [Fact]
    public void Decode_RepeatRun_FillsRepeatedByte()
    {
        byte[] compressed = [unchecked((byte)-5), 9];
        var output = new byte[6];

        TiffPackBitsDecoder.Decode(compressed, output);

        Assert.Equal([9, 9, 9, 9, 9, 9], output);
    }

    [Fact]
    public void Decode_NegativeOneTwentyEight_IsNoOp()
    {
        byte[] compressed = [unchecked((byte)-128), 3, 1, 2, 3, 4];
        var output = new byte[4];

        TiffPackBitsDecoder.Decode(compressed, output);

        Assert.Equal([1, 2, 3, 4], output);
    }

    [Fact]
    public void Decode_MixedLiteralAndRepeatRuns()
    {
        byte[] compressed = [1, 10, 20, unchecked((byte)-2), 99, 0, 42];
        var output = new byte[6];

        TiffPackBitsDecoder.Decode(compressed, output);

        Assert.Equal([10, 20, 99, 99, 99, 42], output);
    }

    [Fact]
    public void Decode_TruncatedLiteralRun_StopsGracefullyWithoutThrowing()
    {
        byte[] compressed = [5, 1, 2]; // Declares 6 literal bytes but only 2 follow.
        var output = new byte[6];

        var exception = Record.Exception(() => TiffPackBitsDecoder.Decode(compressed, output));

        Assert.Null(exception);
        Assert.Equal(1, output[0]);
        Assert.Equal(2, output[1]);
    }

    [Fact]
    public void Decode_TruncatedRepeatRun_MissingValueByte_StopsGracefullyWithoutThrowing()
    {
        byte[] compressed = [unchecked((byte)-3)]; // Repeat control byte with no value byte following.
        var output = new byte[4];

        var exception = Record.Exception(() => TiffPackBitsDecoder.Decode(compressed, output));

        Assert.Null(exception);
    }

    [Fact]
    public void Decode_OutputSmallerThanDeclaredRun_StopsAtOutputBoundary()
    {
        byte[] compressed = [9, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]; // 10 literal bytes.
        var output = new byte[4];

        TiffPackBitsDecoder.Decode(compressed, output);

        Assert.Equal([1, 2, 3, 4], output);
    }
}
