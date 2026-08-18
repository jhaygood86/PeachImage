using System.Numerics;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// VP8's boolean (binary arithmetic) entropy encoder - RFC 6386 section 7.3, the write-side mirror of
/// <see cref="Decoding.Vp8.Vp8BoolDecoder"/>. Writes a sequence of bits, each with its own probability of being
/// 0, into a byte buffer that <see cref="Decoding.Vp8.Vp8BoolDecoder"/> can decode back bit-for-bit.
/// </summary>
/// <remarks>
/// <para>
/// Transcribed from libwebp's <c>src/utils/bit_writer_utils.c</c> (<c>VP8PutBit</c>, <c>Flush</c>,
/// <c>VP8PutBitUniform</c>, <c>VP8PutBits</c>, <c>VP8BitWriterFinish</c>), cross-checked against the downloaded
/// upstream source rather than reconstructed from memory. libwebp's writer and this codebase's
/// <see cref="Decoding.Vp8.Vp8BoolDecoder"/> share the same range bias: both store <c>range</c> as the true
/// range minus 1 (0..254 representing a true range of 1..255), and both compute
/// <c>split = (range * probability) &gt;&gt; 8</c> against that biased value — so <c>split</c> here is exactly
/// what <see cref="Decoding.Vp8.Vp8BoolDecoder.GetBit"/>'s local <c>split</c> variable is before its own +1
/// adjustments, and the two sides' range/value bookkeeping line up term for term. See
/// <see cref="Decoding.Vp8.Vp8BoolDecoder.GetBit"/>'s remarks for the algebra.
/// </para>
/// <para>
/// The one piece with no decoder analogue is carry propagation in <see cref="Flush"/>: bytes equal to 0xFF are
/// not written immediately (they might still need to become 0x00 if a later carry ripples back into them), so a
/// run of them is counted in <see cref="_run"/> and only emitted once a following byte resolves whether the
/// carry actually happened.
/// </para>
/// </remarks>
internal sealed class Vp8BoolEncoder
{
    private byte[] _buffer = WebpBufferPool.Shared.Rent(256);
    private int _length;

    /// <summary>The coder's range, stored biased by -1, matching <see cref="Decoding.Vp8.Vp8BoolDecoder"/>.</summary>
    private int _range;

    private int _value;

    /// <summary>Count of pending output bytes that resolved to 0xFF and are withheld until a later byte resolves whether a carry ripples into them.</summary>
    private int _run;

    /// <summary>Bits currently held in <see cref="_value"/> above the next byte boundary, minus 8 (mirrors <see cref="Decoding.Vp8.Vp8BoolDecoder"/>'s <c>_bits</c> field, but counts up instead of down).</summary>
    private int _bits;

    public Vp8BoolEncoder()
    {
        _range = 255 - 1;
        _value = 0;
        _run = 0;
        _bits = -8;
    }

    /// <summary>Writes a single bit whose probability of being 0 is <paramref name="probability"/> (in [1,255]).</summary>
    public void PutBit(int bit, int probability)
    {
        int split = (_range * probability) >> 8;
        if (bit != 0)
        {
            _value += split + 1;
            _range -= split + 1;
        }
        else
        {
            _range = split;
        }

        if (_range < 127)
        {
            int trueRange = _range + 1;
            int shift = 7 - BitOperations.Log2((uint)trueRange);
            trueRange <<= shift;
            _range = trueRange - 1;
            _value <<= shift;
            _bits += shift;
            if (_bits > 0)
            {
                Flush();
            }
        }
    }

    /// <summary>Writes a single bit with a fixed 50/50 probability (probability = 128).</summary>
    public void PutFlag(bool bit) => PutBit(bit ? 1 : 0, 128);

    /// <summary>Writes <paramref name="numBits"/> unsigned, equiprobable bits, MSB first — the write-side mirror of <see cref="Decoding.Vp8.Vp8BoolDecoder.GetValue"/>.</summary>
    public void PutValue(uint value, int numBits)
    {
        for (int i = numBits - 1; i >= 0; i--)
        {
            PutBit((int)((value >> i) & 1), 128);
        }
    }

    /// <summary>Writes an unsigned magnitude of <paramref name="numBits"/> bits followed by a sign flag bit (1 = negative) — the write-side mirror of <see cref="Decoding.Vp8.Vp8BoolDecoder.GetSignedValue"/>.</summary>
    public void PutSignedValue(int value, int numBits)
    {
        PutValue((uint)Math.Abs(value), numBits);
        PutFlag(value < 0);
    }

    /// <summary>
    /// Walks a flat VP8/VP9-style coding tree to write <paramref name="value"/> as a sequence of branch bits —
    /// the write-side mirror of <see cref="Decoding.Vp8.Vp8BoolDecoder.GetTreeIndex"/>. <paramref name="tree"/>
    /// and <paramref name="probabilities"/> have the same layout <c>GetTreeIndex</c> documents. Unlike the
    /// decoder (which discovers the path by reading bits), the encoder already knows the destination leaf, so
    /// this searches the tree once per call for the branch sequence leading to it. VP8's trees are small
    /// (at most a few dozen nodes) and this is never called from a per-coefficient hot path (coefficient and
    /// mode encoding use their own hand-unrolled cascades, mirroring the decoder's own hand-unrolled decode
    /// cascades), so a plain search is preferable to precomputing and caching a path table.
    /// </summary>
    public void PutTreeIndex(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities, int value, int start = 0)
    {
        Span<int> nodePath = stackalloc int[16];
        Span<int> bitPath = stackalloc int[16];
        int depth = 0;
        bool found = FindPath(tree, start, (sbyte)(-value), nodePath, bitPath, ref depth);
        if (!found)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value is not a leaf of the given tree.");
        }

        for (int i = 0; i < depth; i++)
        {
            PutBit(bitPath[i], probabilities[nodePath[i] >> 1]);
        }
    }

    private static bool FindPath(ReadOnlySpan<sbyte> tree, int nodeIndex, sbyte targetLeaf, Span<int> nodePath, Span<int> bitPath, ref int depth)
    {
        for (int branch = 0; branch < 2; branch++)
        {
            sbyte entry = tree[nodeIndex + branch];
            if (entry == targetLeaf)
            {
                nodePath[depth] = nodeIndex;
                bitPath[depth] = branch;
                depth++;
                return true;
            }

            if (entry > 0)
            {
                nodePath[depth] = nodeIndex;
                bitPath[depth] = branch;
                depth++;
                if (FindPath(tree, entry, targetLeaf, nodePath, bitPath, ref depth))
                {
                    return true;
                }

                depth--;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts one output byte from <see cref="_value"/> once enough bits have accumulated, transcribed
    /// verbatim from libwebp's <c>Flush</c>. The extracted byte's bit 0x100 signals a carry that must ripple
    /// into the most recently written byte (safe because any buffered byte is, by construction, always less
    /// than 0xFF — a byte that resolves to 0xFF is instead counted in <see cref="_run"/> and withheld, since it
    /// might still need to become 0x00 if a later carry reaches it).
    /// </summary>
    private void Flush()
    {
        int s = 8 + _bits;
        int bits = _value >> s;
        _value -= bits << s;
        _bits -= 8;

        if ((bits & 0xff) != 0xff)
        {
            if ((bits & 0x100) != 0 && _length > 0)
            {
                _buffer[_length - 1]++;
            }

            if (_run > 0)
            {
                byte fill = (bits & 0x100) != 0 ? (byte)0x00 : (byte)0xff;
                EnsureCapacity(_length + _run);
                for (; _run > 0; _run--)
                {
                    _buffer[_length++] = fill;
                }
            }

            EnsureCapacity(_length + 1);
            _buffer[_length++] = (byte)(bits & 0xff);
        }
        else
        {
            _run++;
        }
    }

    /// <summary>
    /// Pads and drains all remaining bits, returning the encoded bytes and returning the internal rented buffer
    /// to the pool — the write-side mirror of <see cref="Decoding.Vp8.Vp8BoolDecoder"/>'s implicit end-of-range
    /// behavior, transcribed from libwebp's <c>VP8BitWriterFinish</c>. Must only be called once, with no further
    /// writes after it, matching every other rented-buffer writer in this codebase (e.g.
    /// <see cref="Vp8L.Vp8LBitWriter"/>).
    /// </summary>
    public byte[] Finish()
    {
        int padBits = 9 - _bits;
        for (int i = 0; i < padBits; i++)
        {
            PutBit(0, 128);
        }

        _bits = 0;
        Flush();

        byte[] result = _buffer.AsSpan(0, _length).ToArray();
        WebpBufferPool.Shared.Return(_buffer);
        return result;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        int newSize = _buffer.Length * 2;
        while (newSize < required)
        {
            newSize *= 2;
        }

        byte[] newBuffer = WebpBufferPool.Shared.Rent(newSize);
        _buffer.AsSpan(0, _length).CopyTo(newBuffer);
        WebpBufferPool.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
