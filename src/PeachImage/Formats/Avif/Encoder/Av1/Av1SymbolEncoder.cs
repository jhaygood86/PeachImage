using System.Numerics;
using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// AV1's multi-symbol adaptive arithmetic encoder (spec §8.2) -- the write-side counterpart to
/// <see cref="Av1SymbolDecoder"/>. AV1, like every normative video/image codec spec, defines only the
/// <em>decoder</em>'s arithmetic; real encoders (libaom included) use their own internal range-coder
/// representation, engineered to produce output <see cref="Av1SymbolDecoder"/> decodes correctly, but the
/// exact fixed-point carry-propagation convention real encoders use is not something this codebase can
/// safely re-derive from memory without risking a subtly wrong (but plausible-looking) implementation --
/// exactly the kind of error this component can least afford, since unlike the forward transform, "close"
/// is not good enough here: every symbol and every CDF-adaptation step must match what
/// <see cref="Av1SymbolDecoder"/> independently computes, bit for bit.
///
/// <para>Instead, this derives the needed bits directly from <see cref="Av1SymbolDecoder"/>'s own update
/// equations, exploiting a key property: which bits get consumed and how many (the "renormalization
/// schedule") depends only on the CDFs and target symbols, never on the bit <em>values</em> themselves
/// (<see cref="Av1SymbolDecoder"/>'s range update is symbol/CDF-driven; only its value-comparison depends
/// on bits). This means the full symbol sequence for a tile can be recorded in a forward pass (computing
/// each step's <c>cur(symbol)</c> and renormalization shift exactly as the decoder would, and adapting CDFs
/// identically), then the concrete bits solved for in a single backward pass over that recording -- working
/// from an arbitrary (always achievable) choice at the very end back to the initial bits, by algebraically
/// inverting each step's rebase (<c>value -= cur</c>) and renormalization (<c>value = (value &lt;&lt; bits) |
/// complement(newBits)</c>) in turn. See <c>Av1SymbolEncoderTests</c> for the correctness gate: every
/// encoded sequence is decoded back through the real, unmodified <see cref="Av1SymbolDecoder"/> and both
/// the symbols and the final CDF state are compared.</para>
/// </summary>
/// <summary>
/// The symbol-writing surface <see cref="Av1CoefficientWriter.WriteCoeffs"/> (and other real per-symbol
/// call sites) writes through -- <see cref="Av1SymbolEncoder"/> implements it for real bitstream output;
/// <see cref="Av1TrialSymbolSink"/> implements it for RD-search cost estimation (see <see cref="Av1RdCost"/>),
/// letting both share the exact same context-derivation code in <see cref="Av1CoefficientWriter"/> instead of
/// a hand-duplicated (and driftable) second copy.
/// </summary>
internal interface IAv1SymbolSink
{
    void WriteSymbol(Span<ushort> cdf, int symbol);

    void WriteLiteral(uint value, int n);
}

/// <summary>
/// An <see cref="IAv1SymbolSink"/> that never writes or adapts anything -- it only accumulates the bit cost
/// <see cref="Av1SymbolEncoder.WriteSymbol"/> would have spent, via <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>,
/// for RD-search candidate costing (see <see cref="Av1RdCost"/>). A literal bit always costs exactly 1 bit
/// (see <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>'s remarks on <see cref="Av1SymbolEncoder.WriteBool"/>'s
/// fixed 50/50 CDF), so <see cref="WriteLiteral"/> adds <c>n</c> directly rather than calling the general
/// estimator <c>n</c> times.
/// </summary>
internal sealed class Av1TrialSymbolSink : IAv1SymbolSink
{
    public long Bits { get; private set; }

    public void WriteSymbol(Span<ushort> cdf, int symbol) => Bits += Av1SymbolEncoder.EstimateSymbolCost(cdf, symbol);

    public void WriteLiteral(uint value, int n) => Bits += n;

    /// <summary>Zeroes <see cref="Bits"/> so this one shared instance (see <c>Av1TileEncoder.TileState.TrialSink</c>) can be reused for the next RD candidate instead of allocating a fresh sink per candidate.</summary>
    public void Reset() => Bits = 0;
}

internal sealed class Av1SymbolEncoder : IAv1SymbolSink
{
    private const int EcProbShift = 6;
    private const int EcMinProb = 4;

    private readonly bool _disableCdfUpdate;
    private readonly List<(uint Cur, int Bits)> _steps = [];

    private uint _symbolRange = 1u << 15;
    private bool _flushed;

    public Av1SymbolEncoder(bool disableCdfUpdate)
    {
        _disableCdfUpdate = disableCdfUpdate;
    }

    /// <summary><c>read_bool()</c>'s write-side counterpart: a fresh, non-adapting 50/50 CDF every call.</summary>
    public void WriteBool(int bit)
    {
        Span<ushort> cdf = [1 << 14, 1 << 15, 0];
        WriteSymbolCore(cdf, bit, adapt: false);
    }

    /// <summary><c>read_literal(n)</c>'s write-side counterpart: n raw bits, MSB first, each via <see cref="WriteBool"/>.</summary>
    public void WriteLiteral(uint value, int n)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            WriteBool((int)((value >> i) & 1));
        }
    }

    /// <summary>
    /// <c>NS(n)</c>'s write-side counterpart (spec §4.10.7, the non-symmetric/truncated-binary unsigned
    /// code) -- the algebraic inverse of <c>Av1TileDecoder.ReadNs</c>/<c>Av1SymbolDecoder</c>'s own copy:
    /// given <c>w = FloorLog2(n) + 1</c> and <c>m = (1 &lt;&lt; w) - n</c>, a decoded <paramref name="value"/>
    /// below <c>m</c> was encoded directly in <c>w - 1</c> bits; a decoded value at or above <c>m</c> was
    /// split into a <c>w - 1</c>-bit prefix and one extra bit, recovered here by solving
    /// <c>value = (prefix &lt;&lt; 1) - m + extraBit</c> for the unique <c>(prefix, extraBit)</c> pair.
    /// </summary>
    public void WriteNs(int value, int n)
    {
        int w = Av1CdfAdaptation.FloorLog2((uint)n) + 1;
        int m = (1 << w) - n;
        if (value < m)
        {
            WriteLiteral((uint)value, w - 1);
            return;
        }

        int t = value + m;
        WriteLiteral((uint)(t >> 1), w - 1);
        WriteLiteral((uint)(t & 1), 1);
    }

    /// <summary>
    /// <c>read_symbol(cdf)</c>'s write-side counterpart: encodes <paramref name="symbol"/> against
    /// <paramref name="cdf"/> and adapts it in place (unless <c>disable_cdf_update</c>), exactly mirroring
    /// <see cref="Av1SymbolDecoder.ReadSymbol"/>'s own adaptation so a real decoder's CDF state stays in
    /// lockstep with what this encoder assumed while writing every subsequent symbol.
    /// </summary>
    public void WriteSymbol(Span<ushort> cdf, int symbol) => WriteSymbolCore(cdf, symbol, adapt: !_disableCdfUpdate);

    private void WriteSymbolCore(Span<ushort> cdf, int symbol, bool adapt)
    {
        if (_flushed)
        {
            throw new InvalidOperationException("Av1SymbolEncoder: cannot write more symbols after Flush() has been called.");
        }

        int n = cdf.Length - 1;

        uint prev = symbol == 0 ? _symbolRange : CurValue(_symbolRange, cdf, symbol - 1, n);
        uint cur = CurValue(_symbolRange, cdf, symbol, n);

        uint newRange = prev - cur;
        int bits = 15 - Av1CdfAdaptation.FloorLog2(newRange);
        _symbolRange = newRange << bits;

        _steps.Add((cur, bits));

        if (adapt)
        {
            Av1CdfAdaptation.AdaptCdf(cdf, n, symbol);
        }
    }

    /// <summary>
    /// Estimates the bits <see cref="WriteSymbol"/> would spend encoding <paramref name="symbol"/> against
    /// <paramref name="cdf"/>, without writing anything or adapting <paramref name="cdf"/> -- the core
    /// primitive behind <see cref="Av1RdCost"/>'s candidate costing. Reuses <see cref="WriteSymbolCore"/>'s
    /// exact renormalization-bit formula (<c>15 - FloorLog2(newRange)</c>), evaluated against the canonical
    /// range every tile starts with (<c>1 &lt;&lt; 15</c>) rather than the instantaneous <see cref="_symbolRange"/>
    /// at whichever point in the real bitstream this symbol will actually land. This is exact, not
    /// approximate, at that starting point, and <see cref="WriteSymbolCore"/>'s own renormalization always
    /// restores <see cref="_symbolRange"/> to <c>[1 &lt;&lt; 15, 1 &lt;&lt; 16)</c> after every single symbol (the
    /// same invariant a real range coder relies on) -- so this differs from the bit count a symbol at any
    /// other point in the tile would really cost by at most a fraction of a bit, the same
    /// canonical-range-instead-of-instantaneous-range approximation real AV1/libaom encoders themselves use
    /// to precompute static per-symbol cost tables for RD search (they don't re-derive cost from the actual
    /// running coder state either). Good enough to rank RD candidates against each other; not a substitute
    /// for <see cref="Flush"/>'s real bit-exact output.
    ///
    /// <para>A literal bit (<see cref="WriteBool"/>'s fixed, non-adapting 50/50 CDF <c>[1 &lt;&lt; 14, 1 &lt;&lt; 15, 0]</c>)
    /// always costs exactly 1 bit under this same formula regardless of which of its two symbol values is
    /// written -- <see cref="Av1TrialSymbolSink.WriteLiteral"/> relies on this to add <c>n</c> directly rather
    /// than calling this method <c>n</c> times.</para>
    /// </summary>
    internal static int EstimateSymbolCost(ReadOnlySpan<ushort> cdf, int symbol)
    {
        const uint canonicalRange = 1u << 15;
        int n = cdf.Length - 1;

        uint prev = symbol == 0 ? canonicalRange : CurValue(canonicalRange, cdf, symbol - 1, n);
        uint cur = CurValue(canonicalRange, cdf, symbol, n);

        uint newRange = prev - cur;
        return 15 - Av1CdfAdaptation.FloorLog2(newRange);
    }

    /// <summary>
    /// Pure bit-cost counterpart to <see cref="WriteNs"/> -- <c>NS(n)</c> is a non-adaptive, literal-only
    /// code (see <see cref="WriteNs"/>'s remarks), so unlike <see cref="EstimateSymbolCost"/> this needs no
    /// canonical-range approximation: the exact same <c>w</c>/<c>m</c> split <see cref="WriteNs"/> would
    /// write always costs exactly this many bits, regardless of any CDF or adaptation state. Used by RD-search
    /// candidate costing (e.g. a speculative palette color-index map) that must estimate an <c>NS</c>-coded
    /// value's cost without actually writing it.
    /// </summary>
    internal static int EstimateNsCost(int value, int n)
    {
        int w = Av1CdfAdaptation.FloorLog2((uint)n) + 1;
        int m = (1 << w) - n;
        return value < m ? w - 1 : w;
    }

    /// <summary><c>cur(idx)</c> exactly as <see cref="Av1SymbolDecoder.ReadSymbolCore"/> computes it for a candidate symbol index.</summary>
    private static uint CurValue(uint range, ReadOnlySpan<ushort> cdf, int idx, int n)
    {
        uint f = (1u << 15) - cdf[idx];
        uint cur = ((range >> 8) * (f >> EcProbShift)) >> (7 - EcProbShift);
        cur += (uint)(EcMinProb * (n - idx - 1));
        return cur;
    }

    /// <summary>
    /// Finalizes the encoded tile and returns its raw bytes -- a valid <see cref="Av1SymbolDecoder"/> input
    /// buffer that decodes back to exactly the symbol sequence written. No further writes are permitted
    /// after this call.
    /// </summary>
    public byte[] Flush()
    {
        _flushed = true;

        // Backward pass: start from the simplest always-achievable choice at the end of the tile (0, which
        // is trivially within [0, finalRange) for any finalRange >= 1), and algebraically invert each
        // step's renormalization and rebase in turn, deriving the concrete bits that must have produced it.
        BigInteger t = BigInteger.Zero;
        var reversedChunks = new List<(BigInteger Value, int Width)>();

        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            (uint cur, int bits) = _steps[i];

            BigInteger mask = (BigInteger.One << bits) - 1;
            BigInteger injectedComplement = t & mask;
            BigInteger injectedRaw = mask ^ injectedComplement; // complement within `bits` width
            reversedChunks.Add((injectedRaw, bits));

            t >>= bits;
            t += cur;
        }

        // `t` now holds the value the initial buffer read must produce. Always use a >= 2 byte tile (forcing
        // numBits == 15 at init, per Av1SymbolDecoder's Math.Min(length*8, 15)) so the low bits of the
        // initial SymbolValue are never subject to Av1BitReader's own end-of-buffer zero padding, which
        // would otherwise force specific bit values this derivation doesn't account for.
        const int initNumBits = 15;
        BigInteger initMask = (BigInteger.One << initNumBits) - 1;
        BigInteger initComplement = t & initMask;
        BigInteger initBuf = initMask ^ initComplement;

        int totalBits = initNumBits;
        foreach ((_, int width) in reversedChunks)
        {
            totalBits += width;
        }

        int length = Math.Max(2, (totalBits + 7) / 8);

        var writer = new Av1BitWriter(length + 1);
        writer.WriteBits((uint)initBuf, initNumBits);
        for (int i = reversedChunks.Count - 1; i >= 0; i--)
        {
            (BigInteger value, int width) = reversedChunks[i];
            writer.WriteBits((uint)value, width);
        }

        byte[] written = writer.ToArray();
        if (written.Length >= length)
        {
            return written;
        }

        // Pad up to the chosen `length` (never read by the decoder -- SymbolMaxBits only ever covers the
        // bits actually consumed by the recorded steps) so the tile's byte length matches what was assumed
        // when computing `length` above.
        var padded = new byte[length];
        Array.Copy(written, padded, written.Length);
        return padded;
    }
}
