using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Packs bits least-significant-bit-first into a growable in-memory buffer — the write-side mirror of
/// <see cref="Decoding.Vp8L.Vp8LBitReader"/>, modeled on <see cref="Gif.Encoding.GifLzwBitWriter"/>'s
/// shift-and-emit shape (minus GIF's 255-byte sub-block framing, which VP8L has no equivalent of).
/// </summary>
/// <remarks>
/// Buffers into memory rather than a <see cref="Stream"/>: the RIFF container needs a VP8L chunk's total
/// byte length before it can write that chunk's own header, and VP8L payloads are small enough that
/// buffering first is simpler than a seek-and-patch stream design. The internal buffer is rented from
/// <see cref="WebpBufferPool.Shared"/> (growing by re-renting a larger buffer rather than
/// <see cref="Array.Resize{T}(ref T[], int)"/>'s allocate-and-copy) and returned to the pool the moment
/// <see cref="ToArray"/> is called — <see cref="ToArray"/> must therefore only be called once per instance,
/// with no further writes after it, the same single-use contract every other rented buffer in this codebase
/// follows.
/// </remarks>
internal sealed class Vp8LBitWriter
{
    private byte[] _buffer = WebpBufferPool.Shared.Rent(256);
    private int _length;
    private ulong _bitBuffer;
    private int _bitCount;

    /// <summary>Writes the low <paramref name="bitCount"/> bits (0-32) of <paramref name="value"/>, least-significant-bit-first.</summary>
    public void WriteBits(uint value, int bitCount)
    {
        if (bitCount == 0)
        {
            return;
        }

        ulong masked = bitCount == 32 ? value : value & ((1u << bitCount) - 1);
        _bitBuffer |= masked << _bitCount;
        _bitCount += bitCount;

        while (_bitCount >= 8)
        {
            EnsureCapacity(_length + 1);
            _buffer[_length++] = (byte)_bitBuffer;
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    /// <summary>Pads and emits any partially-filled trailing byte. Safe to call more than once.</summary>
    public void Flush()
    {
        if (_bitCount > 0)
        {
            EnsureCapacity(_length + 1);
            _buffer[_length++] = (byte)_bitBuffer;
            _bitBuffer = 0;
            _bitCount = 0;
        }
    }

    /// <summary>Flushes any pending bits, returns the written bytes, and returns the internal rented buffer to the pool. Must only be called once.</summary>
    public byte[] ToArray()
    {
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
