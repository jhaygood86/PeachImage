namespace PeachImage.Formats.Jpeg.Markers;

/// <summary>
/// Buffered forward-only byte reader over a <see cref="Stream"/>, shared by <see cref="JpegMarkerReader"/> and
/// the entropy-coded segment reader. Supports pushing back up to two bytes, which the entropy reader uses to
/// hand an unconsumed marker (<c>0xFF</c> + code) back to marker-level parsing once it detects one.
/// </summary>
internal sealed class JpegByteSource(Stream stream)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _bufferStart;
    private int _bufferEnd;
    private readonly byte[] _pending = new byte[2];
    private int _pendingCount;
    private long _position;

    /// <summary>The number of bytes consumed from the stream so far, for diagnostics.</summary>
    public long BytePosition => _position;

    /// <summary>Attempts to read the next byte, returning <see langword="false"/> at end of stream.</summary>
    public bool TryReadByte(out byte value)
    {
        if (_pendingCount > 0)
        {
            value = _pending[0];
            if (_pendingCount == 2)
            {
                _pending[0] = _pending[1];
            }

            _pendingCount--;
            _position++;
            return true;
        }

        if (_bufferStart >= _bufferEnd && !Refill())
        {
            value = 0;
            return false;
        }

        value = _buffer[_bufferStart++];
        _position++;
        return true;
    }

    /// <summary>Reads the next byte, throwing <see cref="JpegDecodingException"/> at end of stream.</summary>
    public byte ReadByteOrThrow()
    {
        if (!TryReadByte(out byte value))
        {
            throw new JpegDecodingException("Unexpected end of stream while reading JPEG data.");
        }

        return value;
    }

    /// <summary>Pushes a byte back so the next <see cref="TryReadByte"/> returns it again. At most two bytes of pushback are supported.</summary>
    public void PushBack(byte value)
    {
        if (_pendingCount == 2)
        {
            throw new InvalidOperationException("JpegByteSource supports pushing back at most two bytes.");
        }

        if (_pendingCount == 1)
        {
            _pending[1] = _pending[0];
        }

        _pending[0] = value;
        _pendingCount++;
        _position--;
    }

    /// <summary>Skips exactly <paramref name="count"/> bytes, throwing if the stream ends first.</summary>
    public void SkipBytes(int count)
    {
        while (count > 0)
        {
            if (_pendingCount > 0)
            {
                TryReadByte(out _);
                count--;
                continue;
            }

            if (_bufferStart >= _bufferEnd && !Refill())
            {
                throw new JpegDecodingException("Unexpected end of stream while skipping JPEG segment data.");
            }

            int available = _bufferEnd - _bufferStart;
            int toSkip = Math.Min(available, count);
            _bufferStart += toSkip;
            _position += toSkip;
            count -= toSkip;
        }
    }

    /// <summary>Copies up to <paramref name="destination"/>'s length bytes into it, returning the number actually copied (fewer than requested only at end of stream).</summary>
    public int ReadSegmentPayload(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (_pendingCount > 0)
            {
                TryReadByte(out byte b);
                destination[total++] = b;
                continue;
            }

            if (_bufferStart >= _bufferEnd && !Refill())
            {
                break;
            }

            int available = _bufferEnd - _bufferStart;
            int toCopy = Math.Min(available, destination.Length - total);
            _buffer.AsSpan(_bufferStart, toCopy).CopyTo(destination[total..]);
            _bufferStart += toCopy;
            _position += toCopy;
            total += toCopy;
        }

        return total;
    }

    private bool Refill()
    {
        _bufferStart = 0;
        _bufferEnd = stream.Read(_buffer, 0, _buffer.Length);
        return _bufferEnd > 0;
    }
}
