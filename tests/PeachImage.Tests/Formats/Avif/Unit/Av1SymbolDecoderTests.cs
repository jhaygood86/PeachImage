using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Verifies <see cref="Av1SymbolDecoder"/> against hand-traced known vectors: expected outputs computed
/// by manually executing the AV1 specification's own §8.2 pseudocode (init_symbol/read_bool/renormalize)
/// step by step on specific byte sequences, independent of (and before writing) the implementation under
/// test -- see the trace for each case in its test method's comment. This is the practical substitute for
/// a differential test against an independent reference decoder, none of which (dav1d/aomdec CLI tools)
/// is available in this environment; the project plan's stronger, integration-level signal comes once the
/// partition tree/mode decode is built on top of this and run against real AVIF files, where a single bit
/// of desync anywhere in this decoder would very likely either crash (running past the tile's byte range)
/// or fail the AV1 bitstream's own trailing-bits conformance check at tile exit.
/// </summary>
public class Av1SymbolDecoderTests
{
    /// <summary>
    /// Trace for input bytes [0x00, 0x00] (sz=2): init_symbol reads 15 zero bits, so
    /// SymbolValue=(1&lt;&lt;15)-1=32767, SymbolRange=32768, SymbolMaxBits=1. The first read_bool() call
    /// (cdf=[16384,32768,0], N=2): iteration symbol=0 computes cur=16388; the loop condition
    /// (SymbolValue &lt; cur) is 32767 &lt; 16388 = false, so the do-while loop exits after exactly one
    /// iteration with symbol=0.
    /// </summary>
    [Fact]
    public void ReadBool_AllZeroInput_FirstCallReturnsZero()
    {
        byte[] data = [0x00, 0x00];
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        Assert.Equal(0, decoder.ReadBool());
    }

    /// <summary>
    /// Trace for input bytes [0xFF, 0xFF] (sz=2): init_symbol reads 15 one-bits (buf=32767), so
    /// SymbolValue=32767^32767=0, SymbolRange=32768, SymbolMaxBits=1. The first read_bool() call:
    /// iteration symbol=0 computes cur=16388; condition 0 &lt; 16388 = true, loop continues. Iteration
    /// symbol=1 computes f=32768-cdf[1]=0, cur=0; condition 0 &lt; 0 = false, loop exits with symbol=1.
    /// </summary>
    [Fact]
    public void ReadBool_AllOnesInput_FirstCallReturnsOne()
    {
        byte[] data = [0xFF, 0xFF];
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        Assert.Equal(1, decoder.ReadBool());
    }

    [Fact]
    public void ReadLiteral_AllZeroInput_ReturnsZero()
    {
        byte[] data = new byte[8];
        var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

        Assert.Equal(0u, decoder.ReadLiteral(8));
    }

    [Fact]
    public void ReadSymbol_NeverThrows_AcrossManyReadsOfSyntheticData()
    {
        // Not a correctness proof by itself (no independent oracle for the decoded values), but a real
        // stress test of the renormalization/adaptation loop across every byte pattern, including the
        // SymbolMaxBits-goes-negative padding path once the input is exhausted.
        for (int seed = 0; seed < 16; seed++)
        {
            var random = new Random(seed);
            byte[] data = new byte[64];
            random.NextBytes(data);

            var decoder = new Av1SymbolDecoder(data, 0, data.Length, disableCdfUpdate: false);

            // A representative small mix of symbol counts, matching real AV1 CDFs' typical alphabet sizes.
            Span<ushort> binaryCdf = [1 << 14, 1 << 15, 0];
            Span<ushort> fourWayCdf = [8192, 16384, 24576, 1 << 15, 0];

            for (int i = 0; i < 500; i++)
            {
                int symbol = i % 2 == 0
                    ? decoder.ReadSymbol(binaryCdf)
                    : decoder.ReadSymbol(fourWayCdf);

                Assert.InRange(symbol, 0, i % 2 == 0 ? 1 : 3);
            }
        }
    }

    [Fact]
    public void Constructor_ZeroLengthInput_DoesNotThrow()
    {
        var decoder = new Av1SymbolDecoder([], 0, 0, disableCdfUpdate: false);
        Assert.True(decoder.SymbolMaxBits < 0);
    }
}
