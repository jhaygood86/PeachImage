using PeachImage.Formats.Gif.Decoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Lzw;

public class GifLzwStalePoolDataTests
{
    [Theory]
    [InlineData(1)] // invalid minimum code size: nothing is decoded
    [InlineData(8)] // valid, but no data: the stream is empty
    public void DecodeInto_NoUsableData_ClearsDirtyOutput(int minCodeSize)
    {
        byte[] output = new byte[64];
        Array.Fill(output, (byte)0xFF);

        GifLzwDecoder.DecodeInto([], 0, minCodeSize, output, pixelCount: 32);

        Assert.All(output.AsSpan(0, 32).ToArray(), b => Assert.Equal(0, b));
        Assert.All(output.AsSpan(32).ToArray(), b => Assert.Equal(0xFF, b));
    }
}
