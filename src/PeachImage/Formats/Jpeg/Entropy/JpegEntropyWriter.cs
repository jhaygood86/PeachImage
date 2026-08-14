namespace PeachImage.Formats.Jpeg.Entropy;

/// <summary>
/// Bit-level writer for a JPEG entropy-coded segment. Stuffs a <c>0x00</c> byte after every literal
/// <c>0xFF</c> data byte, mirroring <see cref="JpegEntropyReader"/>'s destuffing.
/// </summary>
/// <remarks>
/// <see cref="_buffer"/> is 64-bit and drains are deferred (see <see cref="DrainCompleteBytes"/>) rather
/// than draining down to a partial byte after every <see cref="WriteBits"/> call — this lets several
/// calls' bits accumulate before a drain, so each drain batches more complete bytes and can bulk-copy
/// them (scanning once for any <c>0xFF</c> needing stuffing) instead of stuffing/appending one byte at a
/// time. <see cref="_bitCount"/> can therefore sit anywhere in <c>[0, 31]</c> between calls, not just
/// <c>[0, 7]</c> — <see cref="Flush"/> accounts for that explicitly rather than assuming a partial byte.
/// </remarks>
internal sealed class JpegEntropyWriter(Stream stream)
{
    /// <summary>
    /// Drain once accumulated bits reach this many — comfortably under the 64-bit ceiling even after one
    /// more maximum-size write (JPEG Huffman codes are Annex-C-capped at 16 bits, and coefficient
    /// magnitude sizes derive from <see cref="short"/>-typed values, so no single <see cref="WriteBits"/>
    /// call ever adds more than 16 bits: <c>(DrainThresholdBits - 1) + 16 &lt;= 64</c>).
    /// </summary>
    private const int DrainThresholdBits = 32;

    private readonly byte[] _outBuffer = new byte[8192];
    private int _outCount;
    private ulong _buffer;
    private int _bitCount;

    /// <summary>Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>, most-significant bit first.</summary>
    public void WriteBits(int value, int bitCount)
    {
        if (bitCount == 0)
        {
            return;
        }

        ulong masked = (uint)value & ((1u << bitCount) - 1);
        _buffer |= masked << (64 - _bitCount - bitCount);
        _bitCount += bitCount;

        if (_bitCount >= DrainThresholdBits)
        {
            DrainCompleteBytes();
        }
    }

    /// <summary>Pads any partial byte with 1-bits and flushes it, leaving the writer byte-aligned.</summary>
    public void Flush()
    {
        int partial = _bitCount % 8;
        if (partial != 0)
        {
            int padBits = 8 - partial;
            WriteBits((1 << padBits) - 1, padBits);
        }

        // Padding above always brings _bitCount to a multiple of 8 (0 if it was already byte-aligned),
        // but WriteBits only drains once DrainThresholdBits is reached — force the rest out unconditionally
        // regardless of how much accumulated, since Flush must leave nothing pending.
        DrainCompleteBytes();
        DrainBuffer();
    }

    /// <summary>Resets bit-accumulation state, e.g. after writing a restart marker.</summary>
    public void Reset()
    {
        _buffer = 0;
        _bitCount = 0;
    }

    /// <summary>
    /// Extracts every complete byte currently in <see cref="_buffer"/> (leaving any partial trailing byte,
    /// 0-7 bits, in place) and writes them out. If none of the extracted bytes is <c>0xFF</c>, bulk-copies
    /// them directly into <see cref="_outBuffer"/> with no per-byte stuffing check — the write-side mirror
    /// of <see cref="Markers.JpegByteSource.ReadCleanRun"/>. Falls back to the original one-at-a-time
    /// <see cref="WriteStuffedByte"/> path only when an <c>0xFF</c> is actually present (rare — only when a
    /// coefficient's magnitude bits happen to produce that byte pattern).
    /// </summary>
    private void DrainCompleteBytes()
    {
        int n = _bitCount / 8;
        if (n == 0)
        {
            return;
        }

        Span<byte> bytes = stackalloc byte[8];
        for (int i = 0; i < n; i++)
        {
            bytes[i] = (byte)(_buffer >> (56 - (i * 8)));
        }

        _buffer <<= n * 8;
        _bitCount -= n * 8;

        if (bytes[..n].IndexOf((byte)0xFF) < 0)
        {
            if (_outCount + n > _outBuffer.Length)
            {
                DrainBuffer();
            }

            bytes[..n].CopyTo(_outBuffer.AsSpan(_outCount));
            _outCount += n;
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                WriteStuffedByte(bytes[i]);
            }
        }
    }

    private void WriteStuffedByte(byte b)
    {
        AppendByte(b);
        if (b == 0xFF)
        {
            AppendByte(0x00);
        }
    }

    private void AppendByte(byte b)
    {
        if (_outCount == _outBuffer.Length)
        {
            DrainBuffer();
        }

        _outBuffer[_outCount++] = b;
    }

    private void DrainBuffer()
    {
        if (_outCount > 0)
        {
            stream.Write(_outBuffer, 0, _outCount);
            _outCount = 0;
        }
    }
}
