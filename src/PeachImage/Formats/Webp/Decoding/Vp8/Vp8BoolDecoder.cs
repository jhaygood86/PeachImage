using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// VP8's boolean (binary arithmetic) entropy decoder - RFC 6386 section 7.3. Reads a sequence of bits, each with
/// its own probability of being 0, out of a fixed byte range within the overall VP8 chunk.
/// </summary>
/// <remarks>
/// <para>
/// This is a mutable reference-type wrapper (not a <c>ref struct</c> over <see cref="ReadOnlySpan{T}"/>) because
/// VP8 keeps many of these decoders alive concurrently for the lifetime of a frame decode - one for partition 0
/// (mode/header data), and one per coefficient-data partition, each revisited once per macroblock row in a
/// round-robin (<c>mbRow % partitionCount</c>) - and the orchestrator needs to hold them all in an array field
/// across many method calls. C# does not allow <c>ref struct</c> instances as array elements, so this type holds
/// a plain <c>byte[]</c> plus start/length offsets instead of a span field.
/// </para>
/// <para>
/// Follows libwebp's <c>src/utils/bit_reader_inl_utils.h</c> rather than the RFC's reference decoder. The two
/// differences that matter are both in <see cref="GetBit"/>: renormalization is a single count-leading-zeros
/// shift instead of a loop that doubles one bit at a time, and the byte supply is refilled 56 bits at a time
/// instead of one byte at a time. See <see cref="GetBit"/>'s remarks for why that produces exactly the same bits
/// as the reference form. Reads past the end of this decoder's byte range synthesize zero bytes rather than
/// throwing, per spec (a partition may be under-read near EOF by design of the entropy coder).
/// </para>
/// <para>
/// One boundary is worth stating explicitly. This form reads an exactly-8-bit window out of the shift register,
/// which is what makes the bulk refill possible; the reference form compares a <c>value</c> accumulator that is
/// only bounded by the range coder's own <c>value &lt; range &lt;&lt; 8</c> invariant. A bitstream whose first
/// byte is <c>0xFF</c> can prime the reference above that bound, after which its accumulator grows without
/// limit and the two forms can disagree. No conformant encoder emits such a stream — it is not a state a range
/// coder can reach — and every one of the 131 libwebp conformance files plus the benchmark assets decodes to
/// identical pixels either way. <c>Vp8BoolDecoderDifferentialTests</c> pins the equivalence, with that one
/// condition spelled out.
/// </para>
/// </remarks>
internal sealed class Vp8BoolDecoder
{
    /// <summary>Bits pulled in per bulk refill. 56 rather than 64 so the 8-byte load always has room above the bits already held.</summary>
    private const int RefillBits = 56;

    private readonly byte[] _buffer;
    private readonly int _start;
    private readonly int _length;

    private int _pos;

    /// <summary>The shift register. Bits at and above <c>_bits + 8</c> are always zero.</summary>
    private ulong _value;

    /// <summary>Valid bits in <see cref="_value"/>, minus 8. Negative means the register needs refilling.</summary>
    private int _bits;

    /// <summary>The coder's range, stored biased by -1 (so 0..254 represents a true range of 1..255).</summary>
    private uint _range;

    public Vp8BoolDecoder(byte[] buffer, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Bounds are already validated by both callers (Vp8FrameHeader.Parse for partition 0, Vp8Partitions.Build
        // for the coefficient partitions), but they are re-checked here because GetBit's bulk refill reads eight
        // bytes at a time: this one check is what makes that read safe without having to re-audit every caller.
        if (start < 0 || length < 0 || start > buffer.Length - length)
        {
            throw new WebpDecodingException($"VP8 bool decoder range [{start}, {start + length}) lies outside the {buffer.Length}-byte chunk.");
        }

        _buffer = buffer;
        _start = start;
        _length = length;
        _pos = 0;

        // -8 rather than 0: the first GetBit triggers the initial load, replacing the reference decoder's eager
        // two-byte prime. Same bits, just fetched lazily.
        _bits = -8;
        _range = 255 - 1;
    }

    /// <summary>Reads a single bit whose probability of being 0 is <paramref name="probability"/> (in [1,255]).</summary>
    /// <remarks>
    /// <para>
    /// Produces bit-for-bit the same sequence as RFC 6386's reference decoder, which computes
    /// <c>split = 1 + (((range-1) * prob) &gt;&gt; 8)</c> against an unbiased range and tests
    /// <c>value &gt;= split &lt;&lt; 8</c>. Holding the range biased by -1 makes <c>split</c> here exactly one
    /// less, so <c>value &gt; split</c> is the same predicate; on a 1 bit the reference's
    /// <c>range - split</c> equals this <c>range - split</c> once the bias is re-applied, and on a 0 bit both
    /// give <c>split + 1</c>.
    /// </para>
    /// <para>
    /// Renormalization is the other half. The reference loop doubles the range until it reaches 128, which is
    /// exactly <c>7 - floor(log2(range))</c> iterations; that count comes straight from
    /// <see cref="BitOperations.Log2(uint)"/> here, and instead of shifting the value up per iteration the read
    /// position moves down, which is equivalent and leaves the register untouched.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBit(int probability)
    {
        // Read the range into a local *before* the refill call. libwebp flags the same ordering as load-bearing
        // for speed, and for the same reason: it keeps the value in a register across a call that never touches it.
        uint range = _range;
        if (_bits < 0)
        {
            LoadNewBytes();
        }

        int pos = _bits;
        uint split = (range * (uint)probability) >> 8;
        uint value = (uint)(_value >> pos);

        int bit;
        if (value > split)
        {
            bit = 1;
            range -= split;
            _value -= (ulong)(split + 1) << pos;
        }
        else
        {
            bit = 0;
            range = split + 1;
        }

        Debug.Assert(range is >= 1 and <= 255, "The coder's true range stays within [1,255], which is what bounds the renormalization shift to [0,7].");

        int shift = 7 - BitOperations.Log2(range);
        range <<= shift;
        _bits -= shift;
        _range = range - 1;

        return bit;
    }

    /// <summary>Pulls <see cref="RefillBits"/> fresh bits into the shift register, or falls back to the tail path near the end of the range.</summary>
    /// <remarks>
    /// Kept out of <see cref="GetBit"/> so the hot path stays a single predictable branch. The 8-byte load is in
    /// bounds because the constructor validated <c>_start + _length &lt;= _buffer.Length</c> and this path
    /// requires eight more bytes before the range's end — bounding it by <see cref="_length"/> rather than by the
    /// array is what matters, since running into the *next* partition would silently decode wrong bits rather
    /// than fault.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LoadNewBytes()
    {
        if (_pos + (sizeof(ulong) - 1) < _length)
        {
            ulong incoming = BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(_start + _pos, sizeof(ulong)));
            _pos += RefillBits >> 3;
            _value = (incoming >> (64 - RefillBits)) | (_value << RefillBits);
            _bits += RefillBits;
            return;
        }

        LoadFinalBytes();
    }

    /// <summary>Byte-at-a-time tail: real bytes while any remain, then synthesized zero bytes indefinitely.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately <em>not</em> libwebp's version of this, which stops advancing after one synthesized zero
    /// byte and pins its bit count. That difference is observable — past the end of a partition the two produce
    /// different bit sequences — and this decoder's existing contract, matching RFC 6386's reference decoder, is
    /// that reads past the range behave as if the buffer continued with zeroes forever. The differential test
    /// against that reference catches the libwebp form immediately at buffer lengths 7, 16 and 17.
    /// </para>
    /// <para>
    /// Synthesizing without bound is safe here because this is only reached when
    /// <see cref="_bits"/> is negative and it adds exactly 8, so <see cref="_bits"/> stays within [0,7] on this
    /// path and the register can never be shifted past its valid width.
    /// </para>
    /// </remarks>
    private void LoadFinalBytes()
    {
        // Reading a real byte and synthesizing a zero one are the same operation with a different incoming byte:
        // shift the register up and drop the new byte into the low 8 bits.
        ulong incoming = _pos < _length ? _buffer[_start + _pos++] : 0u;
        _value = incoming | (_value << 8);
        _bits += 8;
    }

    /// <summary>Reads a single bit with a fixed 50/50 probability (probability = 128).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetFlag() => GetBit(128) != 0;

    /// <summary>Reads <paramref name="numBits"/> unsigned, equiprobable bits, MSB first.</summary>
    public uint GetValue(int numBits)
    {
        uint v = 0;
        for (int i = 0; i < numBits; i++)
        {
            v = (v << 1) | (uint)GetBit(128);
        }

        return v;
    }

    /// <summary>Reads an unsigned magnitude of <paramref name="numBits"/> bits followed by a sign flag bit (1 = negative).</summary>
    public int GetSignedValue(int numBits)
    {
        int magnitude = (int)GetValue(numBits);
        return GetFlag() ? -magnitude : magnitude;
    }

    /// <summary>
    /// Walks a flat VP8/VP9-style coding tree: <paramref name="tree"/> stores pairs of child entries per node
    /// (a non-negative entry is the index of a child node, a non-positive entry is <c>-token</c> for a terminal
    /// leaf), and <paramref name="probabilities"/>[i] is the probability of taking the 0-branch out of node
    /// <c>i</c>. Starts at tree index <paramref name="start"/> (always an even node-pair offset).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetTreeIndex(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities, int start = 0)
    {
        int i = start;
        int next;
        while ((next = tree[i + GetBit(probabilities[i >> 1])]) > 0)
        {
            i = next;
        }

        return -next;
    }
}
