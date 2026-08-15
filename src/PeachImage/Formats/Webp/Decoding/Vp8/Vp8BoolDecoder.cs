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
/// Implements the straightforward byte-at-a-time renormalization from the RFC's reference decoder (not
/// libwebp's wider multi-byte-refill optimization) - correctness first. Reads past the end of this decoder's
/// byte range synthesize zero bytes rather than throwing, per spec (a partition may be under-read near EOF by
/// design of the entropy coder).
/// </para>
/// </remarks>
internal sealed class Vp8BoolDecoder
{
    private readonly byte[] _buffer;
    private readonly int _start;
    private readonly int _length;

    private int _pos;
    private uint _range;
    private uint _value;
    private int _bitCount;

    public Vp8BoolDecoder(byte[] buffer, int start, int length)
    {
        _buffer = buffer;
        _start = start;
        _length = length;
        _pos = 0;
        _range = 255;
        _bitCount = 0;
        _value = ((uint)NextByte() << 8) | NextByte();
    }

    private byte NextByte()
    {
        if ((uint)_pos < (uint)_length)
        {
            return _buffer[_start + _pos++];
        }

        return 0;
    }

    /// <summary>Reads a single bit whose probability of being 0 is <paramref name="probability"/> (in [1,255]).</summary>
    public int GetBit(int probability)
    {
        uint split = 1u + (((_range - 1u) * (uint)probability) >> 8);
        uint bigSplit = split << 8;

        int bit;
        if (_value >= bigSplit)
        {
            bit = 1;
            _range -= split;
            _value -= bigSplit;
        }
        else
        {
            bit = 0;
            _range = split;
        }

        while (_range < 128)
        {
            _value <<= 1;
            _range <<= 1;
            if (++_bitCount == 8)
            {
                _bitCount = 0;
                _value |= NextByte();
            }
        }

        return bit;
    }

    /// <summary>Reads a single bit with a fixed 50/50 probability (probability = 128).</summary>
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