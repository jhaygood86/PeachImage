using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>
/// A write-only <see cref="Stream"/> that buffers compressed bytes and flushes them as individual
/// CRC'd IDAT chunks to <paramref name="destination"/> once <see cref="PngEncodingLimits.IdatChunkSize"/>
/// bytes have accumulated, so encoding never needs the entire compressed output in memory at once.
/// <see cref="Flush"/> is intentionally a no-op (a mid-stream <c>ZLibStream</c> flush shouldn't force a
/// short IDAT chunk); call <see cref="FinishFinalChunk"/> once encoding is complete to emit any
/// remaining buffered bytes as the last IDAT chunk.
/// </summary>
internal sealed class ChunkedIdatWriterStream(Stream destination) : Stream
{
    private readonly byte[] _buffer = new byte[PngEncodingLimits.IdatChunkSize];
    private int _bufferedCount;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int space = _buffer.Length - _bufferedCount;
            int toCopy = Math.Min(space, buffer.Length);
            buffer[..toCopy].CopyTo(_buffer.AsSpan(_bufferedCount));
            _bufferedCount += toCopy;
            buffer = buffer[toCopy..];

            if (_bufferedCount == _buffer.Length)
            {
                FlushChunk();
            }
        }
    }

    /// <summary>Emits any remaining buffered bytes as the final IDAT chunk. Must be called once, after the compressor has been fully drained and disposed.</summary>
    public void FinishFinalChunk()
    {
        if (_bufferedCount > 0)
        {
            FlushChunk();
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void FlushChunk()
    {
        PngChunkWriter.WriteChunk(destination, PngChunkType.Idat, _buffer.AsSpan(0, _bufferedCount));
        _bufferedCount = 0;
    }
}
