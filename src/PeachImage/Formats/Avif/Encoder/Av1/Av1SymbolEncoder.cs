using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

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
///
/// <para>A mutable <see langword="struct"/> (not a class) specifically so <see cref="Av1CoefficientWriter.WriteCoeffs{TSink}"/>'s
/// generic <c>TSink</c> instantiation over this type gets fully devirtualized/inlined by the JIT -- this is
/// the RD-search hot path (tens of millions of calls for a real image), profiled to spend the large
/// majority of its time inside <c>WriteCoeffs</c> itself; the CLR always JIT-compiles a specialized,
/// non-shared instantiation per distinct value-type generic argument, eliminating the interface dispatch a
/// class-typed sink would still pay on every <see cref="WriteSymbol"/>/<see cref="WriteLiteral"/> call.
/// Callers pass this by <see langword="ref"/> everywhere (see call sites in <c>Av1TileEncoder</c>) so the
/// same accumulator instance -- not a copy -- keeps mutating across a candidate's many sub-block
/// <c>WriteCoeffs</c> calls, exactly as the old class's reference semantics did.</para>
/// </summary>
internal struct Av1TrialSymbolSink : IAv1SymbolSink
{
    public long Bits { get; private set; }

    public void WriteSymbol(Span<ushort> cdf, int symbol) => Bits += Av1SymbolEncoder.EstimateSymbolCost(cdf, symbol);

    public void WriteLiteral(uint value, int n) => Bits += n;

    /// <summary>Zeroes <see cref="Bits"/> so this one shared instance (see <c>Av1TileEncoder.TileState.TrialSink</c>) can be reused for the next RD candidate instead of allocating a fresh sink per candidate.</summary>
    public void Reset() => Bits = 0;
}

/// <summary>
/// An <see cref="IAv1SymbolSink"/> that accumulates real per-symbol RD cost the same way
/// <see cref="Av1TrialSymbolSink"/> does, but -- unlike that sink, which deliberately never adapts anything,
/// matching real AV1 encoders' own static-per-candidate-snapshot cost architecture (confirmed by directly
/// reading libaom's own RD-cost source, see <see cref="Av1SymbolEncoder.EstimateSymbolCost"/>'s remarks) --
/// also adapts whatever CDF array it's handed, exactly like a real commit's own
/// <see cref="Av1SymbolEncoder.WriteSymbol"/> (<c>adapt: true</c>) does.
///
/// <para>Only ever safe to use against a SCRATCH CDF that's reseeded fresh from the tile's real, persistent
/// one before every candidate's own trial, never the real CDF itself -- a speculative candidate that might
/// not be chosen must never leave a trace in real state (the same invariant
/// <see cref="Av1CoefficientWriter.PlaneContext.SeedFrom"/>'s own remarks describe for coefficient
/// contexts). This exists specifically for <see cref="Av1TileEncoder"/>'s
/// <c>ComputeLosslessWholeLeafCostPerSubBlock</c> (see its own remarks): a candidate spanning many
/// (potentially 1024) 4x4 sub-blocks needs its own trial to reflect how quickly a real commit's CDF would
/// adapt across those same sub-blocks -- a literally-flat plane's true cost is dominated almost entirely by
/// that adaptation, which no amount of per-symbol cost precision alone (<see cref="Av1TrialSymbolSink"/>'s
/// own improvement) can model. Every other cost estimate in this codebase keeps using the plain, non-adapting
/// <see cref="Av1TrialSymbolSink"/>, since that rare, once-per-128x128-superblock call site is the only one
/// this gap was ever measured to matter for.</para>
/// </summary>
internal sealed class Av1AdaptingTrialSymbolSink : IAv1SymbolSink
{
    // Accumulated in Av1SymbolEncoder.EstimateSymbolCostPrecise512ths's own native 1/512-bit units, not
    // rounded per symbol the way Bits (and every other IAv1SymbolSink in this codebase) is -- see that
    // method's own remarks for why this specific sink needs that extra precision (summing potentially
    // 1024+ already-whole-bit-rounded symbol costs measurably compounds rounding error, enough to produce
    // spurious exact ties between two genuinely different candidates).
    private long _units512;

    public long Bits => (long)Math.Round(_units512 / 512.0, MidpointRounding.AwayFromZero);

    public void WriteSymbol(Span<ushort> cdf, int symbol)
    {
        _units512 += Av1SymbolEncoder.EstimateSymbolCostPrecise512ths(cdf, symbol);
        Av1CdfAdaptation.AdaptCdf(cdf, cdf.Length - 1, symbol);
    }

    public void WriteLiteral(uint value, int n) => _units512 += (long)n * 512;

    /// <summary>Zeroes the accumulated cost so this one shared instance (see <c>Av1TileEncoder.TileState.AdaptingTrialSink</c>) can be reused across candidates -- the scratch CDF it adapts must be separately reseeded per candidate too, see the call site.</summary>
    public void Reset() => _units512 = 0;
}

/// <summary>
/// AV1's multi-symbol adaptive arithmetic encoder (spec §8.2) -- the write-side counterpart to
/// <see cref="Av1SymbolDecoder"/>. AV1, like every normative video/image codec spec, defines only the
/// <em>decoder</em>'s arithmetic; real encoders (libaom included) use their own internal range-coder
/// representation, engineered to produce output <see cref="Av1SymbolDecoder"/> decodes correctly. The AV1
/// range decoder accepts more than one bit-exact encoding of the same logical symbol sequence, so matching
/// libaom's own output byte-for-byte (this codebase's own stated goal, see the AVIF lossless-parity plan)
/// requires implementing libaom's own specific encoding choice, not just <em>a</em> spec-valid one.
/// </summary>
/// <remarks>
/// <para>This is a direct, literal port of libaom's real forward, incremental, carry-propagating range
/// encoder, <c>aom_dsp/entenc.c</c>/<c>entenc.h</c> (<c>od_ec_enc_normalize</c>/<c>od_ec_encode_q15</c>/
/// <c>od_ec_enc_done</c>/<c>propagate_carry_bwd</c>) -- see <c>THIRD-PARTY-LICENSES.md</c> for the full
/// attribution. libaom represents the coder state as a growing <c>low</c> accumulator plus a shrinking
/// <c>rng</c> (the AV1 spec's own decoder instead tracks a single shrinking <c>SymbolValue</c> within
/// <c>[0, SymbolRange)</c> -- the two are the standard dual "low+range encoder"/"shrinking-interval decoder"
/// formulations of the same range coder, always consistent with each other by construction as long as the
/// range narrows identically on both sides). This class's own private <c>CurValue</c> helper (mirroring
/// <see cref="Av1SymbolDecoder.ReadSymbolCore"/>'s own <c>cur()</c> exactly) already computes the same
/// interval-narrowing terms libaom's own <c>od_ec_encode_q15</c> computes as <c>u</c>/<c>v</c> -- verified
/// algebraically (the two formulations' differing constant-offset terms cancel out in the
/// <c>u - v</c>/<c>prev - cur</c> subtraction) -- so this reuses <c>CurValue</c> unchanged and only replaces
/// the surrounding <c>low</c>/<c>rng</c>/<c>cnt</c> state machine and byte-emission strategy with libaom's
/// own.</para>
///
/// <para>An earlier version of this class instead recorded every symbol's renormalization step during
/// encoding without emitting any bytes, then algebraically solved backward from an arbitrary
/// always-achievable final state for <em>some</em> valid bit sequence. That approach round-tripped correctly
/// through <see cref="Av1SymbolDecoder"/> (both approaches are spec-legal) but did not produce libaom's own
/// specific byte sequence -- confirmed via this project's own libaom byte-exact comparison harness
/// (<c>tools/PeachImage.LibaomParity/</c>), which is what motivated this rewrite. See
/// <c>Av1SymbolEncoderTests</c> for the correctness gate: every encoded sequence is decoded back through the
/// real, unmodified <see cref="Av1SymbolDecoder"/> and both the symbols and the final CDF state are
/// compared.</para>
/// </remarks>
internal sealed class Av1SymbolEncoder : IAv1SymbolSink
{
    private const int EcProbShift = 6;
    private const int EcMinProb = 4;

    private readonly bool _disableCdfUpdate;
    private readonly List<byte> _output = [];

    // Mirrors libaom's od_ec_enc: `low` is the 64-bit growing-window accumulator (od_ec_enc_window),
    // `rng` the 16-bit shrinking range (od_ec_enc_reset: rng = 0x8000), `cnt` the signed count of bits
    // of `low` not yet known to be ready for output (od_ec_enc_reset: cnt = -9, "so that it crosses zero
    // after we've accumulated one byte + one carry bit").
    private ulong _low;
    private uint _rng = 1u << 15;
    private int _cnt = -9;
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

        uint prev = symbol == 0 ? _rng : CurValue(_rng, cdf, symbol - 1, n);
        uint cur = CurValue(_rng, cdf, symbol, n);

        // od_ec_encode_q15 (entenc.c): fl < CDF_PROB_TOP (symbol > 0, using the ascending-cumulative `prev`
        // in place of libaom's own inverted-icdf `fl`) grows `low` by the newly-excluded upper interval and
        // narrows `rng` to the selected sub-interval; fl >= CDF_PROB_TOP (symbol == 0) only narrows `rng`,
        // leaving `low` untouched. Algebraically identical to libaom's own u/v formulation -- see this
        // class's own remarks for why CurValue's differing constant-offset convention cancels out here.
        ulong low = _low;
        uint newRange;
        if (symbol > 0)
        {
            low += _rng - prev;
            newRange = prev - cur;
        }
        else
        {
            newRange = _rng - cur;
        }

        Normalize(low, newRange);

        if (adapt)
        {
            Av1CdfAdaptation.AdaptCdf(cdf, n, symbol);
        }
    }

    /// <summary>
    /// Ported from libaom's <c>od_ec_enc_normalize</c> (<c>aom_dsp/entenc.c</c>): renormalizes
    /// <paramref name="low"/>/<paramref name="rng"/> back into range after a symbol update, and -- whenever
    /// enough bits of <paramref name="low"/> have become permanently fixed (<c>cnt</c> crossing the 40-bit
    /// threshold) -- commits the now-final top bytes of <paramref name="low"/> to the output, propagating any
    /// carry into already-written bytes first.
    /// </summary>
    private void Normalize(ulong low, uint rng)
    {
        int d = 15 - Av1CdfAdaptation.FloorLog2(rng); // libaom's own `16 - OD_ILOG_NZ(rng)`.
        int c = _cnt;
        int s = c + d;

        if (s >= 40)
        {
            int numBytesReady = (s >> 3) + 1;
            c += 24 - (numBytesReady << 3);
            ulong output = low >> c;
            low &= (1UL << c) - 1;
            ulong mask = 1UL << (numBytesReady << 3);
            bool carry = (output & mask) != 0;
            output &= mask - 1;

            EmitBytes(output, numBytesReady, carry);

            s = c + d - 24;
        }

        _low = low << d;
        _rng = rng << d;
        _cnt = s;
    }

    /// <summary>
    /// Ported from libaom's <c>write_enc_data_to_out_buf</c> (<c>aom_dsp/entenc.h</c>): appends the
    /// <paramref name="numBytesReady"/> big-endian bytes of <paramref name="output"/>, then -- if
    /// <paramref name="carry"/> -- propagates a carry backward into the byte immediately preceding this
    /// group (libaom writes a full 8-byte register via <c>memcpy</c> as a performance optimization; this
    /// writes exactly the meaningful bytes one at a time instead, which is functionally identical).
    /// </summary>
    private void EmitBytes(ulong output, int numBytesReady, bool carry)
    {
        int groupStart = _output.Count;
        for (int i = numBytesReady - 1; i >= 0; i--)
        {
            _output.Add((byte)(output >> (i * 8)));
        }

        if (carry)
        {
            PropagateCarryBackward(groupStart - 1);
        }
    }

    /// <summary>Ported verbatim from libaom's <c>propagate_carry_bwd</c> (<c>aom_dsp/entenc.h</c>).</summary>
    private void PropagateCarryBackward(int offset)
    {
        while (true)
        {
            int sum = _output[offset] + 1;
            _output[offset] = (byte)sum;
            if ((sum >> 8) == 0)
            {
                return;
            }

            offset--;
        }
    }

    /// <summary>
    /// Estimates the bits <see cref="WriteSymbol"/> would spend encoding <paramref name="symbol"/> against
    /// <paramref name="cdf"/>, without writing anything or adapting <paramref name="cdf"/> -- the core
    /// primitive behind <see cref="Av1RdCost"/>'s candidate costing.
    ///
    /// <para>This used to reuse <see cref="WriteSymbolCore"/>'s own renormalization-bit formula
    /// (<c>15 - FloorLog2(newRange)</c>) directly -- exact for the real bitstream's own renormalization
    /// schedule, but a poor RD-search proxy for a near-certain, frequently-repeated symbol (the common case
    /// for a highly self-similar lossless leaf, e.g. a periodic all-zero-coefficient run): renormalization
    /// only changes in whole-bit steps at power-of-two range boundaries, so a symbol whose real marginal
    /// entropy is a small fraction of a bit still got charged at least the next whole-bit step, and summed
    /// across many (potentially hundreds of) sub-blocks in one candidate's trial this compounded into a
    /// large, systematic overestimate -- confirmed directly against real <c>aomenc</c> output via this
    /// project's own libaom byte-exact comparison harness (a 128x128 8px-period checkerboard: a single 64x64
    /// candidate's own estimate under the old formula already exceeded aomenc's entire 4-quadrant frame
    /// total).</para>
    ///
    /// <para>Real AV1 encoders (libaom included) don't use their own renormalization schedule as an RD proxy
    /// either, for exactly this reason -- confirmed directly by reading libaom's own source, not
    /// independently re-derived: this now ports libaom's actual <c>av1_cost_symbol</c>
    /// (<c>av1/encoder/cost.h</c>) fixed-point <c>-log2(p)</c> cost model, backed by the real
    /// <c>av1_prob_cost[128]</c> table (<c>av1/encoder/cost.c</c>, ported verbatim below) -- a genuine
    /// fractional-bit estimate (natively in units of 1/512 bit, <c>AV1_PROB_COST_SHIFT</c>) that correctly
    /// prices a near-certain symbol at a small fraction of a bit instead of rounding up to the next
    /// renormalization step. Converted back to whole bits (rounded to nearest, not floored/ceiled -- unlike
    /// the old formula's one-sided upward bias, rounding to nearest is unbiased on average across many
    /// symbols) to keep this method's existing <c>int</c>, "whole bits" contract unchanged for every one of
    /// its many call sites (partition/mode/palette/coefficient costing throughout
    /// <see cref="Av1TileEncoder"/>) -- this is a pure internal-precision improvement, not a new API.</para>
    ///
    /// <para>Deliberately keeps <see cref="WriteSymbolCore"/>'s own real, bit-exact range-coder math (and its
    /// <c>CurValue</c>/renormalization formula) completely untouched -- this change is scoped entirely to the
    /// RD-estimation path, never the real bitstream write path, which must keep matching
    /// <see cref="Av1SymbolDecoder"/> bit-for-bit regardless of how costs are estimated.</para>
    ///
    /// <para>A literal bit (<see cref="WriteBool"/>'s fixed, non-adapting 50/50 CDF <c>[1 &lt;&lt; 14, 1 &lt;&lt; 15, 0]</c>)
    /// still costs exactly 1 bit under this new formula too (verified: a 50/50 split's probability mass is
    /// exactly <c>1 &lt;&lt; 14</c>, which <see cref="Av1ProbCostSymbol"/> maps to exactly <c>512</c> --
    /// libaom's own <c>av1_cost_literal(1)</c> -- i.e. exactly 1 bit after the /512 conversion) --
    /// <see cref="Av1TrialSymbolSink.WriteLiteral"/> still relies on this to add <c>n</c> directly rather
    /// than calling this method <c>n</c> times.</para>
    /// </summary>
    internal static int EstimateSymbolCost(ReadOnlySpan<ushort> cdf, int symbol)
        => (int)Math.Round(EstimateSymbolCostPrecise512ths(cdf, symbol) / 512.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The same libaom-ported <c>av1_cost_symbol</c> estimate <see cref="EstimateSymbolCost"/> exposes, but
    /// in its native 1/512-bit fixed-point units (<c>AV1_PROB_COST_SHIFT</c>) instead of rounded whole bits.
    /// <see cref="EstimateSymbolCost"/> itself just rounds this once and returns -- fine for every ordinary,
    /// single-symbol-at-a-time cost comparison in this codebase, where one extra half-bit of rounding noise
    /// per symbol is immaterial. <see cref="Av1AdaptingTrialSymbolSink"/> uses this method directly instead,
    /// accumulating many (potentially 1024+) symbols' worth of raw 1/512ths before ever rounding -- summing
    /// already-rounded whole-bit costs that many times compounds rounding error enough to matter for that
    /// sink's specific job (ranking two genuinely close candidates against each other), confirmed by this
    /// project's own libaom byte-exact comparison harness: an early version of that sink, built on the
    /// rounded <see cref="EstimateSymbolCost"/> directly, measured an exact tie between two intra modes whose
    /// real, committed entropy costs differed by roughly 6x once one of the tied candidates was actually
    /// picked and encoded -- summing this method's own unrounded units instead broke that spurious tie.
    /// </summary>
    internal static int EstimateSymbolCostPrecise512ths(ReadOnlySpan<ushort> cdf, int symbol)
    {
        uint p15 = symbol == 0 ? cdf[0] : (uint)(cdf[symbol] - cdf[symbol - 1]);
        if (p15 < EcMinProb)
        {
            // Matches libaom's own av1_cost_tokens_from_cdf clamp (av1/encoder/cost.c) -- a symbol whose
            // real, adapted probability mass would fall below the range coder's own reserved minimum
            // (EcMinProb, the same constant CurValue's renormalization already reserves per remaining
            // symbol) can't actually occur at a lower effective probability than that reservation allows.
            p15 = EcMinProb;
        }

        return Av1ProbCostSymbol(p15);
    }

    /// <summary>
    /// Ported from libaom's own <c>av1_cost_symbol</c> (<c>av1/encoder/cost.h</c>): the real fixed-point
    /// <c>-log2(p15 / 32768)</c> cost estimate real AV1 encoders use for RD search, in units of 1/512 bit
    /// (<c>AV1_PROB_COST_SHIFT = 9</c>). <paramref name="p15"/> is a symbol's probability mass out of 32768
    /// (spec's <c>CDF_PROB_TOP</c>), e.g. <c>cdf[symbol] - cdf[symbol - 1]</c> against this project's own
    /// ascending-cumulative cdf array convention (ordinary, not libaom's own internal inverted-icdf storage
    /// -- no translation needed, since this project's own ported CDF tables already normalized that away).
    /// Normalizes <paramref name="p15"/> so its most-significant bit lands at position 14 (matching libaom's
    /// own <c>shift = CDF_PROB_BITS - 1 - get_msb(p15)</c>, <c>get_msb</c> == <see cref="Av1CdfAdaptation.FloorLog2"/>),
    /// reduces that to an 8-bit table index via libaom's own <c>get_prob</c> rounding formula
    /// (<c>round(num * 256 / 32768)</c>, always landing in [128, 255] once normalized this way), and looks up
    /// the fractional remainder in <see cref="Av1ProbCostTable"/>, adding back the whole-1/512-bit-unit cost
    /// of the normalizing shift itself (<c>shift &lt;&lt; 9</c>, libaom's own <c>av1_cost_literal(shift)</c>).
    /// </summary>
    private static int Av1ProbCostSymbol(uint p15)
    {
        p15 = Math.Clamp(p15, 1u, (1u << 15) - 1);
        int shift = 14 - Av1CdfAdaptation.FloorLog2(p15);
        uint num = p15 << shift;
        ulong scaled = ((ulong)num * 256) + (32768 / 2);
        int prob = (int)(scaled / 32768);
        prob = Math.Clamp(prob, 1, 255);
        return Av1ProbCostTable[prob - 128] + (shift << 9);
    }

    /// <summary>
    /// Ported verbatim from libaom's own <c>av1/encoder/cost.c</c>: <c>av1_prob_cost[128]</c>, i.e.
    /// <c>round(-log2(i / 256.0) * (1 &lt;&lt; AV1_PROB_COST_SHIFT))</c> for <c>i = 128..255</c> (index 0 here
    /// is libaom's <c>i = 128</c>). See <see cref="Av1ProbCostSymbol"/>'s remarks for how the index into this
    /// table is derived.
    /// </summary>
    private static readonly int[] Av1ProbCostTable =
    [
        512, 506, 501, 495, 489, 484, 478, 473, 467, 462, 456, 451, 446, 441, 435,
        430, 425, 420, 415, 410, 405, 400, 395, 390, 385, 380, 375, 371, 366, 361,
        356, 352, 347, 343, 338, 333, 329, 324, 320, 316, 311, 307, 302, 298, 294,
        289, 285, 281, 277, 273, 268, 264, 260, 256, 252, 248, 244, 240, 236, 232,
        228, 224, 220, 216, 212, 209, 205, 201, 197, 194, 190, 186, 182, 179, 175,
        171, 168, 164, 161, 157, 153, 150, 146, 143, 139, 136, 132, 129, 125, 122,
        119, 115, 112, 109, 105, 102, 99, 95, 92, 89, 86, 82, 79, 76, 73,
        70, 66, 63, 60, 57, 54, 51, 48, 45, 42, 38, 35, 32, 29, 26,
        23, 20, 18, 15, 12, 9, 6, 3,
    ];

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

        // Ported verbatim from libaom's od_ec_enc_done (aom_dsp/entenc.c): chooses the smallest value `e`
        // congruent to a 0x4000 boundary that is >= the true low `l`, plus a half-step safety margin, as the
        // final committed value, then extracts its remaining bytes (most-significant first) exactly as
        // od_ec_enc_normalize's own flush step would, propagating carry the same way.
        ulong l = _low;
        int c = _cnt;
        int s = 10;
        const ulong m = 0x3FFF;
        ulong e = ((l + m) & ~m) | (m + 1);
        s += c;

        if (s > 0)
        {
            ulong n = (1UL << (c + 16)) - 1;
            do
            {
                ushort val = (ushort)(e >> (c + 16));
                _output.Add((byte)(val & 0xFF));
                if ((val & 0x100) != 0)
                {
                    PropagateCarryBackward(_output.Count - 2);
                }

                e &= n;
                s -= 8;
                c -= 8;
                n >>= 8;
            }
            while (s > 0);
        }

        return _output.ToArray();
    }
}
