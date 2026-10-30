using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// AV1's multi-symbol adaptive arithmetic decoder (spec §8.2). Structurally different from VP8's binary
/// bool-decoder: instead of one bit against a single split probability, it decodes one of N symbols
/// against a cumulative-distribution-function array, and that CDF adapts after every read (unless
/// disable_cdf_update is set). Follows the AV1 specification's own normative arithmetic (§8.2.2
/// init_symbol, §8.2.3 read_bool, §8.2.5 read_literal, §8.2.6 read_symbol/decode_symbol including
/// renormalization and CDF adaptation) exactly; only the way renormalization fetches its input bits differs.
/// </summary>
/// <remarks>
/// Mutable reference type (not a <c>ref struct</c>), matching <c>Vp8BoolDecoder</c>'s own reasoning: a
/// tile's decode is a deeply recursive call chain (partition tree -> mode info -> coefficients), and the
/// decoder instance needs to be reachable throughout without threading it as a ref parameter everywhere.
/// The spec pulls <c>bits</c> fresh bits from the tile data one at a time on every renormalization; this
/// decoder instead keeps a 64-bit left-aligned window of upcoming bits that is refilled a byte at a time
/// (bulk refill, as libaom/dav1d do) and shifted out in a single operation. Bytes past the end of the tile
/// read as zero, which is exactly what the spec's <c>SymbolMaxBits</c> zero-padding rule prescribes, so the
/// decoded values are bit-identical. <see cref="SymbolMaxBits"/> still tracks the spec's own accounting.
/// See <c>Av1SymbolDecoderTests</c> for the verification strategy.
/// </remarks>
internal sealed class Av1SymbolDecoder
{
    private const int EcProbShift = 6;
    private const int EcMinProb = 4;

    private readonly byte[] _data;
    private readonly int _end;
    private readonly bool _disableCdfUpdate;

    private int _pos;
    private ulong _window;
    private int _windowBits;

    private uint _symbolValue;
    private uint _symbolRange;

    public Av1SymbolDecoder(byte[] data, int start, int length, bool disableCdfUpdate)
    {
        _data = data;
        _pos = start;
        _end = start + length;
        _disableCdfUpdate = disableCdfUpdate;

        Refill();

        // The first 15 bits of the (zero-extended) tile data, per spec §8.2.2.
        uint buf = (uint)(_window >> 49);
        _window <<= 15;
        _windowBits -= 15;
        _symbolValue = ((1u << 15) - 1) ^ buf;
        _symbolRange = 1u << 15;
    }

    /// <summary>Bits still available to read from the underlying byte range, per spec's <c>SymbolMaxBits</c> -- negative once all real bits are exhausted and only implicit zero-padding remains.</summary>
    public int SymbolMaxBits => (8 * (_end - _pos)) + _windowBits;

    /// <summary><c>read_bool()</c> (spec §8.2.3): a fresh, non-adapting 50/50 CDF every call.</summary>
    public int ReadBool()
    {
        // read_symbol over the fixed CDF {1 << 14, 1 << 15}, specialised: symbol 0 is chosen when
        // SymbolValue >= cur, where cur = ((range >> 8) * (16384 >> 6) >> 1) + EcMinProb.
        uint range = _symbolRange;
        uint cur = ((range >> 8) << 7) + EcMinProb;
        // Branchless: the outcome is a coin flip, so a branch here mispredicts about half the time.
        uint value = _symbolValue;
        uint zero = value >= cur ? uint.MaxValue : 0u;
        _symbolRange = cur + ((range - cur - cur) & zero);
        _symbolValue = value - (cur & zero);

        Renormalize();
        return (int)(zero & 1) ^ 1;
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
    /// the live, per-tile mutable CDF array (see <c>Av1CdfContext</c>) -- entries
    /// <c>[0, N)</c> are the AV1-convention cumulative distribution and entry <c>N</c> is the adaptation
    /// counter the spec's CDF-update process describes.
    /// </summary>
    public int ReadSymbol(Span<ushort> cdf)
    {
        int n = cdf.Length - 1;
        ref ushort cdfRef = ref MemoryMarshal.GetReference(cdf);

        // Binary and four-symbol alphabets dominate coefficient coding (skip / eob / sign flags, coeff_base,
        // coeff_br), so they get unrolled search-and-adapt paths with identical arithmetic.
        if (n == 4)
        {
            return ReadSymbol4(ref cdfRef);
        }

        if (n == 2)
        {
            return ReadSymbol2(ref cdfRef);
        }

        uint range = _symbolRange;
        uint value = _symbolValue;
        uint cur = range;
        uint prev;
        int symbol = -1;

        // The last real CDF entry is 1 << 15, which gives cur == 0 at symbol n - 1, so the loop always
        // terminates within the array.
        do
        {
            symbol++;
            prev = cur;
            uint f = (1u << 15) - Unsafe.Add(ref cdfRef, symbol);
            cur = (((range >> 8) * (f >> EcProbShift)) >> (7 - EcProbShift)) + (uint)(EcMinProb * (n - symbol - 1));
        }
        while (value < cur);

        _symbolRange = prev - cur;
        _symbolValue = value - cur;

        Renormalize();

        if (!_disableCdfUpdate)
        {
            Av1CdfAdaptation.AdaptCdf(cdf, n, symbol);
        }

        return symbol;
    }

    private int ReadSymbol4(ref ushort c)
    {
        uint range = _symbolRange;
        uint value = _symbolValue;
        uint r = range >> 8;

        // v_i = ((r * ((32768 - cdf[i]) >> 6)) >> 1) + EcMinProb * (3 - i); v_3 == 0 as cdf[3] == 32768.
        uint v0 = ((r * ((32768u - c) >> EcProbShift)) >> 1) + (EcMinProb * 3);
        int symbol;
        uint prev;
        uint cur;
        if (value >= v0)
        {
            symbol = 0;
            prev = range;
            cur = v0;
        }
        else
        {
            uint v1 = ((r * ((32768u - Unsafe.Add(ref c, 1)) >> EcProbShift)) >> 1) + (EcMinProb * 2);
            if (value >= v1)
            {
                symbol = 1;
                prev = v0;
                cur = v1;
            }
            else
            {
                uint v2 = ((r * ((32768u - Unsafe.Add(ref c, 2)) >> EcProbShift)) >> 1) + EcMinProb;
                if (value >= v2)
                {
                    symbol = 2;
                    prev = v1;
                    cur = v2;
                }
                else
                {
                    symbol = 3;
                    prev = v2;
                    cur = 0;
                }
            }
        }

        _symbolRange = prev - cur;
        _symbolValue = value - cur;
        Renormalize();

        if (!_disableCdfUpdate)
        {
            int count = Unsafe.Add(ref c, 4);
            int rate = 5 + (count > 15 ? 1 : 0) + (count > 31 ? 1 : 0);
            ref ushort e1 = ref Unsafe.Add(ref c, 1);
            ref ushort e2 = ref Unsafe.Add(ref c, 2);
            c = symbol > 0 ? (ushort)(c - (c >> rate)) : (ushort)(c + ((32768 - c) >> rate));
            e1 = symbol > 1 ? (ushort)(e1 - (e1 >> rate)) : (ushort)(e1 + ((32768 - e1) >> rate));
            e2 = symbol > 2 ? (ushort)(e2 - (e2 >> rate)) : (ushort)(e2 + ((32768 - e2) >> rate));
            if (count < 32)
            {
                Unsafe.Add(ref c, 4) = (ushort)(count + 1);
            }
        }

        return symbol;
    }

    private int ReadSymbol2(ref ushort c)
    {
        uint range = _symbolRange;
        uint value = _symbolValue;

        uint v0 = (((range >> 8) * ((32768u - c) >> EcProbShift)) >> 1) + EcMinProb;
        int symbol;
        if (value >= v0)
        {
            symbol = 0;
            _symbolRange = range - v0;
            _symbolValue = value - v0;
        }
        else
        {
            symbol = 1;
            _symbolRange = v0;
        }

        Renormalize();

        if (!_disableCdfUpdate)
        {
            int count = Unsafe.Add(ref c, 2);
            int rate = 4 + (count > 15 ? 1 : 0) + (count > 31 ? 1 : 0);
            c = symbol > 0 ? (ushort)(c - (c >> rate)) : (ushort)(c + ((32768 - c) >> rate));
            if (count < 32)
            {
                Unsafe.Add(ref c, 2) = (ushort)(count + 1);
            }
        }

        return symbol;
    }

    /// <summary>The renormalization steps following every symbol decode (spec §8.2.6, the seven ordered steps after the do-while loop).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Renormalize()
    {
        int bits = BitOperations.LeadingZeroCount(_symbolRange) - 16;
        _symbolRange <<= bits;

        if (_windowBits < bits)
        {
            Refill();
        }

        uint newData = (uint)((_window >> 32) >> (32 - bits));
        _window <<= bits;
        _windowBits -= bits;

        _symbolValue = newData ^ (((_symbolValue + 1) << bits) - 1);
    }

    /// <summary>
    /// Tops the window up to at least 57 bits; bytes past the end of the tile data are zero. <c>_pos</c> keeps
    /// counting past <c>_end</c> so the remaining-bit accounting stays exact. The bulk path ORs in a whole
    /// big-endian word: the bits beyond the whole bytes accounted for are the true next bits, so OR-ing
    /// them again on the next refill is idempotent.
    /// </summary>
    private void Refill()
    {
        if (_pos + 8 <= _end)
        {
            _window |= BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(_pos)) >> _windowBits;
            int bytes = (64 - _windowBits) >> 3;
            _pos += bytes;
            _windowBits += bytes << 3;
            return;
        }

        while (_windowBits <= 56)
        {
            ulong b = _pos < _end ? _data[_pos] : 0UL;
            _pos++;
            _window |= b << (56 - _windowBits);
            _windowBits += 8;
        }
    }
}
