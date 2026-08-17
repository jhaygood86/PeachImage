namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// AV1's multi-symbol adaptive arithmetic decoder (spec §8.2). Structurally different from VP8's binary
/// bool-decoder: instead of one bit against a single split probability, it decodes one of N symbols
/// against a cumulative-distribution-function array, and that CDF adapts after every read (unless
/// disable_cdf_update is set). Transcribed directly from the AV1 specification's own normative pseudocode
/// (§8.2.2 init_symbol, §8.2.3 read_bool, §8.2.5 read_literal, §8.2.6 read_symbol/decode_symbol including
/// renormalization and CDF adaptation) rather than a third-party reference implementation.
/// </summary>
/// <remarks>
/// Mutable reference type (not a <c>ref struct</c>), matching <c>Vp8BoolDecoder</c>'s own reasoning: a
/// tile's decode is a deeply recursive call chain (partition tree -> mode info -> coefficients), and the
/// decoder instance needs to be reachable throughout without threading it as a ref parameter everywhere.
/// This is a direct, unoptimized transcription of the spec's own algorithm rather than the faster
/// daala/libaom/dav1d-style bulk-refill reformulation the project plan suggested as a later option --
/// correctness comes first (this decoder's Phase 2 priority), and a from-scratch reformulation would add
/// exactly the kind of transcription risk this component most needs to avoid. See
/// <c>Av1SymbolDecoderTests</c> for the verification strategy: hand-traced known input/output vectors
/// (computed by manually executing the spec pseudocode on specific byte sequences, independent of this
/// implementation) plus invariant checks over both synthetic and real tile data, since no independent
/// reference AV1 decoder is available in this environment to differentially test against.
/// </remarks>
internal sealed class Av1SymbolDecoder
{
    private const int EcProbShift = 6;
    private const int EcMinProb = 4;

    private readonly Av1BitReader _bits;
    private readonly bool _disableCdfUpdate;

    private uint _symbolValue;
    private uint _symbolRange;
    private int _symbolMaxBits;

    public Av1SymbolDecoder(byte[] data, int start, int length, bool disableCdfUpdate)
    {
        _bits = new Av1BitReader(data, start, length);
        _disableCdfUpdate = disableCdfUpdate;

        int numBits = Math.Min(length * 8, 15);
        uint buf = _bits.ReadBits(numBits);
        uint paddedBuf = buf << (15 - numBits);
        _symbolValue = ((1u << 15) - 1) ^ paddedBuf;
        _symbolRange = 1u << 15;
        _symbolMaxBits = (8 * length) - 15;
    }

    /// <summary>Bits still available to read from the underlying byte range, per spec's <c>SymbolMaxBits</c> -- negative once all real bits are exhausted and only implicit zero-padding remains.</summary>
    public int SymbolMaxBits => _symbolMaxBits;

    /// <summary><c>read_bool()</c> (spec §8.2.3): a fresh, non-adapting 50/50 CDF every call.</summary>
    public int ReadBool()
    {
        Span<ushort> cdf = [1 << 14, 1 << 15, 0];
        return ReadSymbolCore(cdf, adapt: false);
    }

    /// <summary><c>read_literal(n)</c> (spec §8.2.5): n raw bits, MSB first, each via <see cref="ReadBool"/>.</summary>
    public uint ReadLiteral(int n)
    {
        uint x = 0;
        for (int i = 0; i < n; i++)
        {
            x = (2 * x) + (uint)ReadBool();
        }

        return x;
    }

    /// <summary>
    /// <c>read_symbol(cdf)</c> (spec §8.2.6): decodes one of <c>cdf.Length - 1</c> symbols and adapts
    /// <paramref name="cdf"/> in place (unless <c>disable_cdf_update</c>). <paramref name="cdf"/> must be
    /// the live, per-tile mutable CDF array (see the future <c>Av1CdfContext</c>) -- entries
    /// <c>[0, N)</c> are the AV1-convention cumulative distribution and entry <c>N</c> is the adaptation
    /// counter the spec's CDF-update process describes.
    /// </summary>
    public int ReadSymbol(Span<ushort> cdf) => ReadSymbolCore(cdf, adapt: !_disableCdfUpdate);

    private int ReadSymbolCore(Span<ushort> cdf, bool adapt)
    {
        int n = cdf.Length - 1;

        uint cur = _symbolRange;
        uint prev;
        int symbol = -1;

        do
        {
            symbol++;
            prev = cur;
            uint f = (1u << 15) - cdf[symbol];
            cur = ((_symbolRange >> 8) * (f >> EcProbShift)) >> (7 - EcProbShift);
            cur += (uint)(EcMinProb * (n - symbol - 1));
        }
        while (_symbolValue < cur);

        _symbolRange = prev - cur;
        _symbolValue -= cur;

        Renormalize();

        if (adapt)
        {
            Av1CdfAdaptation.AdaptCdf(cdf, n, symbol);
        }

        return symbol;
    }

    /// <summary>The renormalization steps following every symbol decode (spec §8.2.6, the seven ordered steps after the do-while loop).</summary>
    private void Renormalize()
    {
        int bits = 15 - FloorLog2(_symbolRange);
        _symbolRange <<= bits;

        int numBits = Math.Min(bits, Math.Max(0, _symbolMaxBits));
        uint newData = numBits > 0 ? _bits.ReadBits(numBits) : 0;
        uint paddedData = newData << (bits - numBits);

        _symbolValue = paddedData ^ (((_symbolValue + 1) << bits) - 1);
        _symbolMaxBits -= bits;
    }

    private static int FloorLog2(uint x) => Av1CdfAdaptation.FloorLog2(x);
}
