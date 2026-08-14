using PeachImage.Formats.Jpeg.Markers;

namespace PeachImage.Formats.Jpeg.Entropy;

/// <summary>
/// Bit-level reader over a JPEG entropy-coded segment. Handles byte-stuffing (<c>0xFF 0x00</c> pairs
/// represent a literal <c>0xFF</c> data byte) and stops cleanly at the next real marker (a restart marker
/// or EOI), leaving that marker unconsumed in the underlying <see cref="JpegByteSource"/> so
/// <see cref="Markers.JpegMarkerReader"/> can read it normally afterwards.
/// </summary>
internal sealed class JpegEntropyReader(JpegByteSource source)
{
    private ulong _buffer;
    private int _bitCount;

    /// <summary>True once a real marker (not byte-stuffed data) has been encountered and no more entropy-coded bits remain.</summary>
    public bool AtMarkerBoundary { get; private set; }

    /// <summary>Reads <paramref name="count"/> bits (0-24) as an unsigned value, most-significant bit first.</summary>
    public int GetBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        EnsureBits(count);
        int value = (int)(_buffer >> (64 - count));
        _buffer <<= count;
        _bitCount -= count;
        return value;
    }

    /// <summary>Reads a single bit. Used for progressive successive-approximation refinement.</summary>
    public int ReceiveBit() => GetBits(1);

    /// <summary>
    /// Returns the next <paramref name="count"/> bits (0-24) without consuming them, for fast-path Huffman
    /// lookahead. Call <see cref="GetBits"/> afterward to actually consume however many bits the lookup resolved.
    /// </summary>
    public int PeekBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        EnsureBits(count);
        return (int)(_buffer >> (64 - count));
    }

    /// <summary>
    /// Implements the JPEG "RECEIVE and EXTEND" procedure (ITU-T.81 F.2.2.1): reads <paramref name="magnitudeBits"/>
    /// bits and sign-extends them into a signed DC difference or AC coefficient value.
    /// </summary>
    public int ReceiveExtend(int magnitudeBits)
    {
        if (magnitudeBits == 0)
        {
            return 0;
        }

        return ExtendSign(GetBits(magnitudeBits), magnitudeBits);
    }

    /// <summary>
    /// The "EXTEND" half of ITU-T.81 F.2.2.1's "RECEIVE and EXTEND" procedure in isolation: sign-extends
    /// <paramref name="rawMagnitudeBits"/> (the raw unsigned bits already read, via whatever means) as a
    /// <paramref name="magnitudeBits"/>-bit-wide DC difference or AC coefficient value. Shared with
    /// <see cref="HuffmanDecodingTable"/>'s combined Huffman+magnitude fast-decode table, which precomputes
    /// this at table-build time for every possible magnitude-bit pattern rather than at decode time — pulled
    /// out to one place specifically so the two call sites can't silently compute it differently.
    /// </summary>
    internal static int ExtendSign(int rawMagnitudeBits, int magnitudeBits)
    {
        int threshold = 1 << (magnitudeBits - 1);
        return rawMagnitudeBits < threshold ? rawMagnitudeBits + (-1 << magnitudeBits) + 1 : rawMagnitudeBits;
    }

    /// <summary>Discards any partially-consumed bits so the next read starts byte-aligned, as required before a restart marker.</summary>
    public void AlignAndDiscardPartialByte()
    {
        _buffer = 0;
        _bitCount = 0;
    }

    /// <summary>Resets bit-reading state after a restart marker has been consumed, so subsequent reads start fresh.</summary>
    public void ResetBitState()
    {
        _buffer = 0;
        _bitCount = 0;
        AtMarkerBoundary = false;
    }

    /// <summary>
    /// Consumes the restart marker expected at the current position (after <see cref="AlignAndDiscardPartialByte"/>
    /// has been called), verifying it matches <paramref name="expectedIndex0To7"/>.
    /// </summary>
    public void ConsumeRestartMarker(int expectedIndex0To7)
    {
        if (!source.TryReadByte(out byte first) || first != 0xFF)
        {
            throw new JpegDecodingException("Expected a restart marker but none was found.");
        }

        byte code;
        do
        {
            if (!source.TryReadByte(out code))
            {
                throw new JpegDecodingException("Unexpected end of stream while expecting a restart marker.");
            }
        }
        while (code == 0xFF);

        byte expected = (byte)(0xD0 + expectedIndex0To7);
        if (code != expected)
        {
            throw new JpegDecodingException($"Expected restart marker 0xFF{expected:X2} but found 0xFF{code:X2}.");
        }
    }

    private void EnsureBits(int count)
    {
        Span<byte> clean = stackalloc byte[8];
        while (_bitCount < count && _bitCount <= 56)
        {
            if (!AtMarkerBoundary)
            {
                // Normally 1-8 (64-bit _buffer, entry guarantees 0 <= _bitCount <= 56 so at least 1 byte
                // fits): the exact number of whole bytes that can be packed in without _bitCount exceeding 64.
                // Clamped defensively: GetBits doesn't validate its caller-supplied count against a malformed
                // stream's Huffman-decoded "size" values before subtracting it from _bitCount, so a
                // sufficiently adversarial file (see the corpus crashtest suite) can already have driven
                // _bitCount negative by the time control reaches here — the same way the original byte-at-a-
                // time EnsureBits tolerated it (via a wraparound shift amount, not a crash), this must not
                // turn that into an out-of-range span slice.
                int maxBulk = Math.Clamp((64 - _bitCount) / 8, 0, 8);
                int read = maxBulk == 0 ? 0 : source.ReadCleanRun(clean[..maxBulk]);
                if (read > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        _buffer |= (ulong)clean[i] << (56 - _bitCount);
                        _bitCount += 8;
                    }

                    continue;
                }
            }

            byte b = 0;
            if (!AtMarkerBoundary && !TryReadStuffedByte(out b))
            {
                AtMarkerBoundary = true;
            }

            _buffer |= (ulong)b << (56 - _bitCount);
            _bitCount += 8;
        }
    }

    private bool TryReadStuffedByte(out byte value)
    {
        if (!source.TryReadByte(out byte b))
        {
            value = 0;
            return false;
        }

        if (b != 0xFF)
        {
            value = b;
            return true;
        }

        if (!source.TryReadByte(out byte next))
        {
            value = 0;
            return false;
        }

        while (next == 0xFF)
        {
            if (!source.TryReadByte(out next))
            {
                value = 0;
                return false;
            }
        }

        if (next == 0x00)
        {
            value = 0xFF;
            return true;
        }

        // A real marker follows: push it back (with its 0xFF prefix) so marker-level parsing can consume it.
        source.PushBack(next);
        source.PushBack(0xFF);
        value = 0;
        return false;
    }
}
