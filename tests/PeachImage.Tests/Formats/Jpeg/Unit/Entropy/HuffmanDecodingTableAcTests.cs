using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Entropy;

/// <summary>
/// Verifies <see cref="HuffmanDecodingTable.DecodeAc"/> (the combined Huffman+magnitude fast path for
/// baseline AC coefficients) against an independent reimplementation of the two-step process it replaces
/// (<see cref="HuffmanDecodingTable.Decode"/> plus a separate <see cref="JpegEntropyReader.ReceiveExtend"/>
/// — the exact shape <c>BaselineScanDecoder.DecodeBlock</c>'s AC loop used before this fast path existed),
/// not by calling <see cref="HuffmanDecodingTable.Decode"/> itself (which <see cref="HuffmanDecodingTable.DecodeAc"/>'s
/// own fallback branch also calls — that would only catch a bug in the fallback dispatch, not in the fast
/// table's own construction).
/// </summary>
public class HuffmanDecodingTableAcTests
{
    /// <summary>
    /// Hand-crafted to hit every interesting boundary in one small table (four length-2 codes):
    /// symbol 0x50 (size=0, run=5 — malformed-but-legal-per-spec EOB, not the standard 0x00; exercises the
    /// design review's fix that any size-0/run!=15 symbol is EOB, not just 0x00), 0xF0 (standard ZRL),
    /// 0x08 (run=0, size=8 — code length 2 + size 8 = 10, exactly <see cref="HuffmanDecodingTable"/>'s
    /// FastTableBits, the combined-fast-path boundary), and 0x19 (run=1, size=9 — length 2 + size 9 = 11,
    /// one over the boundary, forcing the fallback path).
    /// </summary>
    private static readonly (byte[] Counts, byte[] Values) BoundaryTable = (
        [0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        [0x50, 0xF0, 0x08, 0x19]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DecodeAc_MatchesTwoStepReference_OnBoundaryTable(int seed)
    {
        var table = HuffmanDecodingTable.Build(BoundaryTable.Counts, BoundaryTable.Values);
        AssertDecodeAcMatchesReference(table, seed, symbolCount: 500);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DecodeAc_MatchesTwoStepReference_OnStandardLuminanceAcTable(int seed)
    {
        var table = HuffmanDecodingTable.Build(StandardHuffmanTables.LuminanceAc.Counts, StandardHuffmanTables.LuminanceAc.Values);
        AssertDecodeAcMatchesReference(table, seed, symbolCount: 2000);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DecodeAc_MatchesTwoStepReference_OnStandardChrominanceAcTable(int seed)
    {
        var table = HuffmanDecodingTable.Build(StandardHuffmanTables.ChrominanceAc.Counts, StandardHuffmanTables.ChrominanceAc.Values);
        AssertDecodeAcMatchesReference(table, seed, symbolCount: 2000);
    }

    /// <summary>
    /// Decodes <paramref name="symbolCount"/> AC symbols from the same random byte stream two independent
    /// ways — <see cref="HuffmanDecodingTable.DecodeAc"/>, and a from-scratch reimplementation of the old
    /// <see cref="HuffmanDecodingTable.Decode"/>+<see cref="JpegEntropyReader.ReceiveExtend"/> two-step —
    /// over two separate <see cref="JpegEntropyReader"/> instances so consuming bits in one never affects
    /// the other, and asserts every decoded symbol (kind, run, value) matches.
    /// </summary>
    private static void AssertDecodeAcMatchesReference(HuffmanDecodingTable table, int seed, int symbolCount)
    {
        var random = new Random(seed);
        var bytes = new byte[symbolCount * 4]; // generous upper bound: worst case ~2 bytes/symbol, doubled for headroom
        random.NextBytes(bytes);

        // Avoid 0xFF entirely: byte-stuffing/marker-boundary handling is covered by JpegEntropyReaderTests
        // and isn't what this test is checking — a stray 0xFF here would trip AtMarkerBoundary and both
        // readers would agree anyway (same input, same logic), but keeping the stream stuffing-free makes
        // failures easier to diagnose by removing an unrelated variable.
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
            var fastKind = table.DecodeAc(fastReader, out int fastRun, out int fastValue);
            var (referenceKind, referenceRun, referenceValue) = DecodeAcReference(table, referenceReader);

            Assert.Equal(referenceKind, fastKind);
            if (fastKind == AcSymbolKind.Coefficient)
            {
                Assert.Equal(referenceRun, fastRun);
                Assert.Equal(referenceValue, fastValue);
            }
        }
    }

    /// <summary>The two-step process <see cref="HuffmanDecodingTable.DecodeAc"/> replaces, reimplemented independently (not calling <see cref="HuffmanDecodingTable.DecodeAc"/> or reusing its internals).</summary>
    private static (AcSymbolKind Kind, int Run, int Value) DecodeAcReference(HuffmanDecodingTable table, JpegEntropyReader reader)
    {
        int runSize = table.Decode(reader);
        int size = runSize & 0xF;
        int run = runSize >> 4;

        if (size == 0)
        {
            return run == 15 ? (AcSymbolKind.ZeroRun16, 0, 0) : (AcSymbolKind.EndOfBlock, 0, 0);
        }

        int value = reader.ReceiveExtend(size);
        return (AcSymbolKind.Coefficient, run, value);
    }
}
