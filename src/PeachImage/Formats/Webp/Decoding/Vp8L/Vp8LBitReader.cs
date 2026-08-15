using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Reads VP8L's bitstream least-significant-bit-first (the same convention as <c>GifLzwBitReader</c>), but
/// widened to a 64-bit shift register: VP8L routinely needs up to ~24 bits in a single <see cref="ReadBits"/>
/// call (e.g. long backward-reference extra bits), so a byte- or int-sized register would need multiple
/// refills per read. <see cref="PeekBits"/>/<see cref="SkipBits"/> are split out (rather than folded into one
/// <c>TryReadCode</c> like Gif's reader) because the Huffman decoder needs to peek a fixed window, look the
/// value up in a table, and then skip only however many bits that particular code actually used.
/// </summary>
/// <remarks>
/// Per the VP8L spec, the bitstream's last byte is implicitly zero-padded — reading past the physical end of
/// the buffer is not itself an error, it just synthesizes zero bits. This reader follows that: it never
/// throws on a short/truncated buffer, it just starts returning zeros once the byte cursor exhausts the
/// buffer. <see cref="IsOverBudget"/> tracks whether more bits have been *consumed* (via <see cref="SkipBits"/>)
/// than the buffer physically contains, which callers can use to detect genuine truncation and stop early
/// (mirroring <c>GifLzwDecoder</c>'s "stop early on truncation" convention) rather than decode nonsense from
/// an all-zero tail forever.
/// </remarks>
internal sealed class Vp8LBitReader
{
    private readonly byte[] _data;
    private readonly int _length;
    private int _bytePos;
    private ulong _bitBuffer;
    private int _bitCount;
    private long _bitsConsumed;
    private readonly long _totalBits;

    /// <param name="data">The backing buffer (may be array-pool-rented and therefore longer than the real data).</param>
    /// <param name="startOffset">The index of the first byte to read.</param>
    /// <param name="endOffset">The exclusive end offset — the index one past the last valid byte of real data in <paramref name="data"/> (not a byte count).</param>
    public Vp8LBitReader(byte[] data, int startOffset, int endOffset)
    {
        _data = data;
        _bytePos = startOffset;
        _length = endOffset;
        _totalBits = (long)(endOffset - startOffset) * 8;
    }

    /// <summary>
    /// <see langword="true"/> once more bits have been consumed than the buffer actually contains — i.e. the
    /// stream is truncated and any symbols decoded from this point on are reading synthesized zero padding,
    /// not real data.
    /// </summary>
    public bool IsOverBudget => _bitsConsumed > _totalBits;

    /// <summary>Returns the next <paramref name="bitCount"/> bits (0-32) without consuming them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int bitCount)
    {
        if (bitCount == 0)
        {
            return 0;
        }

        EnsureBits(bitCount);
        return (uint)(_bitBuffer & ((1UL << bitCount) - 1));
    }

    /// <summary>Consumes <paramref name="bitCount"/> bits previously observed via <see cref="PeekBits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int bitCount)
    {
        _bitBuffer >>= bitCount;
        _bitCount -= bitCount;
        _bitsConsumed += bitCount;
    }

    /// <summary>Reads and consumes the next <paramref name="bitCount"/> bits (0-32), least-significant-bit-first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int bitCount)
    {
        uint value = PeekBits(bitCount);
        SkipBits(bitCount);
        return value;
    }

    /// <summary>The check that keeps <see cref="Refill"/> off the hot path; split so the common "already have enough bits" case inlines into the caller as a single compare.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureBits(int bitCount)
    {
        if (_bitCount < bitCount)
        {
            Refill(bitCount);
        }
    }

    /// <summary>
    /// Tops the shift register up by a whole 32-bit word at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The previous bulk path required <c>_bitCount == 0</c> <em>exactly</em>. Because <see cref="SkipBits"/>
    /// consumes whatever a Huffman code happened to be worth, the register essentially never lands on exactly
    /// empty, so in practice almost every refill fell through to the byte-at-a-time loop below — the bulk load
    /// was close to dead code. Refilling 32 bits instead of 64 is what removes the precondition: the register
    /// is 64 bits wide and a request is at most 32, so <c>_bitCount &lt;= 31</c> whenever this is reached and
    /// the fresh word always fits above the bits already there. It also means a single refill is always
    /// enough, since <c>_bitCount + 32 &gt;= 32 &gt;= bitCount</c>. This is the same 32-into-64 arrangement
    /// libwebp's <c>VP8LDoFillBitWindow</c> uses.
    /// </para>
    /// <para>
    /// The <c>|=</c> relies on an invariant maintained everywhere else in this type: bits at and above
    /// <see cref="_bitCount"/> in <see cref="_bitBuffer"/> are always zero. The register starts zeroed,
    /// <see cref="SkipBits"/> shifts right (which zero-fills from the top), and both paths here OR in at
    /// exactly <see cref="_bitCount"/>.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill(int bitCount)
    {
        Debug.Assert(bitCount is > 0 and <= 32, "VP8L never requests more than 32 bits at once.");
        Debug.Assert(_bitCount < 32, "A refill below 32 buffered bits is what guarantees the 32-bit word fits.");
        Debug.Assert(_bitCount == 64 || (_bitBuffer >> _bitCount) == 0, "Bits above _bitCount must be zero for the OR below to be lossless.");

        if (_bytePos + 4 <= _length)
        {
            _bitBuffer |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_bytePos, 4)) << _bitCount;
            _bytePos += 4;
            _bitCount += 32;
            return;
        }

        // Tail-byte fallback: top up one byte at a time (real bytes while available, then implicit zero
        // padding once the buffer is exhausted) until there are enough bits to satisfy the request.
        while (_bitCount < bitCount)
        {
            byte next = _bytePos < _length ? _data[_bytePos++] : (byte)0;
            _bitBuffer |= (ulong)next << _bitCount;
            _bitCount += 8;
        }
    }
}
