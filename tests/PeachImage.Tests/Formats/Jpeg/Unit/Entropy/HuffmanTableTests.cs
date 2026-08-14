using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Entropy;

public class HuffmanTableTests
{
    [Fact]
    public void StandardLuminanceDcTable_EncodesThenDecodesEverySymbol()
    {
        AssertRoundTripsAllSymbols(StandardHuffmanTables.LuminanceDc.Counts, StandardHuffmanTables.LuminanceDc.Values);
    }

    [Fact]
    public void StandardChrominanceDcTable_EncodesThenDecodesEverySymbol()
    {
        AssertRoundTripsAllSymbols(StandardHuffmanTables.ChrominanceDc.Counts, StandardHuffmanTables.ChrominanceDc.Values);
    }

    [Fact]
    public void StandardLuminanceAcTable_EncodesThenDecodesEverySymbol()
    {
        AssertRoundTripsAllSymbols(StandardHuffmanTables.LuminanceAc.Counts, StandardHuffmanTables.LuminanceAc.Values);
    }

    [Fact]
    public void StandardChrominanceAcTable_EncodesThenDecodesEverySymbol()
    {
        AssertRoundTripsAllSymbols(StandardHuffmanTables.ChrominanceAc.Counts, StandardHuffmanTables.ChrominanceAc.Values);
    }

    [Fact]
    public void Decode_OfDegenerateSingleSymbolTable_Works()
    {
        // A table with exactly one 1-bit code (length-1 count = 1, everything else 0) is the simplest
        // valid Huffman table: the single symbol is always coded as the bit '0'.
        byte[] counts = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] values = [42];

        var (stream, _) = EncodeBits([(0, 1)]);
        var decodingTable = HuffmanDecodingTable.Build(counts, values);
        var reader = MakeReader(stream);

        Assert.Equal(42, decodingTable.Decode(reader));
    }

    [Fact]
    public void Decode_OfInvalidCode_ThrowsJpegDecodingException()
    {
        // A table with no codes at all: any bit read cannot match any code length, so decode must fail cleanly
        // rather than loop forever or read out of bounds.
        byte[] counts = new byte[16];
        byte[] values = [];
        var decodingTable = HuffmanDecodingTable.Build(counts, values);

        var stream = new MemoryStream([0xFF, 0xD9]); // arbitrary bytes; content doesn't matter, decode must throw before consuming meaning
        var reader = MakeReader(stream);

        Assert.Throws<JpegDecodingException>(() => decodingTable.Decode(reader));
    }

    private static void AssertRoundTripsAllSymbols(byte[] counts, byte[] values)
    {
        var encodingTable = HuffmanEncodingTable.Build(counts, values);
        var decodingTable = HuffmanDecodingTable.Build(counts, values);

        foreach (byte symbol in values)
        {
            using var ms = new MemoryStream();
            var writer = new JpegEntropyWriter(ms);
            encodingTable.Encode(writer, symbol);
            writer.Flush();

            ms.Position = 0;
            var reader = MakeReader(ms);
            int decoded = decodingTable.Decode(reader);

            Assert.Equal(symbol, decoded);
        }
    }

    private static (MemoryStream Stream, byte[] Bytes) EncodeBits((int Value, int BitCount)[] bits)
    {
        var ms = new MemoryStream();
        var writer = new JpegEntropyWriter(ms);
        foreach (var (value, bitCount) in bits)
        {
            writer.WriteBits(value, bitCount);
        }

        writer.Flush();
        ms.Position = 0;
        return (ms, ms.ToArray());
    }

    private static JpegEntropyReader MakeReader(Stream stream) => new(new JpegByteSource(stream));
}
