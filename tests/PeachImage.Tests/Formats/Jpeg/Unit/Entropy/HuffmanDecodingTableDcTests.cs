using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Entropy;

/// <summary>
/// Verifies <see cref="HuffmanDecodingTable.DecodeDcDiff"/> (the combined Huffman+magnitude fast path for
/// DC coefficients — the same technique as <see cref="HuffmanDecodingTable.DecodeAc"/>, minus the run/
/// EOB/ZRL dispatch, since a DC symbol is always a bare magnitude size) against an independent
/// reimplementation of the two-step process it replaces (<see cref="HuffmanDecodingTable.Decode"/> plus a
/// separate <see cref="JpegEntropyReader.ReceiveExtend"/>), the same verification shape
/// <see cref="HuffmanDecodingTableAcTests"/> uses for the AC case.
/// </summary>
public class HuffmanDecodingTableDcTests
{
    /// <summary>
    /// Hand-crafted with four length-2 codes to hit the interesting boundaries: size=0 (code `00` — no
    /// magnitude bits at all, diff is always 0, matching <see cref="JpegEntropyReader.ReceiveExtend"/>'s
    /// early return), size=8 (code `01` — length 2 + size 8 = 10, exactly <see cref="HuffmanDecodingTable"/>'s
    /// FastTableBits, the combined-fast-path boundary), size=9 (code `10` — length 2 + size 9 = 11, one
    /// over the boundary, forcing the fallback path), and size=1 (code `11` — a short, common case).
    /// </summary>
    private static readonly (byte[] Counts, byte[] Values) BoundaryTable = (
        [0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0, 8, 9, 1]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DecodeDcDiff_MatchesTwoStepReference_OnBoundaryTable(int seed)
    {
        var table = HuffmanDecodingTable.Build(BoundaryTable.Counts, BoundaryTable.Values);
        AssertDecodeDcDiffMatchesReference(table, seed, symbolCount: 500);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DecodeDcDiff_MatchesTwoStepReference_OnStandardLuminanceDcTable(int seed)
    {
        var table = HuffmanDecodingTable.Build(StandardHuffmanTables.LuminanceDc.Counts, StandardHuffmanTables.LuminanceDc.Values);
        AssertDecodeDcDiffMatchesReference(table, seed, symbolCount: 2000);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DecodeDcDiff_MatchesTwoStepReference_OnStandardChrominanceDcTable(int seed)
    {
        var table = HuffmanDecodingTable.Build(StandardHuffmanTables.ChrominanceDc.Counts, StandardHuffmanTables.ChrominanceDc.Values);
        AssertDecodeDcDiffMatchesReference(table, seed, symbolCount: 2000);
    }

    private static void AssertDecodeDcDiffMatchesReference(HuffmanDecodingTable table, int seed, int symbolCount)
    {
        var random = new Random(seed);
        var bytes = new byte[symbolCount * 4];
        random.NextBytes(bytes);

        // Stuffing-free stream, same reasoning as HuffmanDecodingTableAcTests: byte-stuffing behavior is
        // covered elsewhere, keeping it out of this stream isolates what this test is actually checking.
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF)
            {
                bytes[i] = 0xFE;
            }
        }

        var fastReader = new JpegEntropyReader(new JpegByteSource(new MemoryStream(bytes)));
        var referenceReader = new JpegEntropyReader(new JpegByteSource(new MemoryStream(bytes)));

        for (int i = 0; i < symbolCount; i++)
        {
            // Random bytes are not a real Huffman-encoded stream: for a table with sparse code assignments
            // (not every bit pattern up to the max code length is a valid code), random noise will
            // eventually produce a pattern neither implementation can resolve. Both must agree that it's
            // invalid at the same point (they share the same underlying Decode/DecodeSlow for exactly this
            // case) rather than the test assuming an unbounded supply of decodable symbols.
            JpegDecodingException? fastException = null;
            int fastValue = 0;
            try
            {
                fastValue = table.DecodeDcDiff(fastReader);
            }
            catch (JpegDecodingException ex)
            {
                fastException = ex;
            }

            JpegDecodingException? referenceException = null;
            int referenceValue = 0;
            try
            {
                referenceValue = DecodeDcDiffReference(table, referenceReader);
            }
            catch (JpegDecodingException ex)
            {
                referenceException = ex;
            }

            if (fastException is not null || referenceException is not null)
            {
                Assert.NotNull(fastException);
                Assert.NotNull(referenceException);
                Assert.Equal(referenceException.Message, fastException.Message);
                break;
            }

            Assert.Equal(referenceValue, fastValue);
        }
    }

    /// <summary>The two-step process <see cref="HuffmanDecodingTable.DecodeDcDiff"/> replaces, reimplemented independently.</summary>
    private static int DecodeDcDiffReference(HuffmanDecodingTable table, JpegEntropyReader reader)
    {
        int size = table.Decode(reader);
        return reader.ReceiveExtend(size);
    }
}
