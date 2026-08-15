using System.Buffers.Binary;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>
/// A forward-only, read-only <see cref="Stream"/> that transparently crosses IDAT chunk boundaries,
/// reading compressed bytes directly from the underlying PNG stream with no buffering of the compressed
/// payload. When the current IDAT chunk is exhausted, its CRC is validated, and the next chunk's header
/// is read: if it's another IDAT chunk, reading continues seamlessly; otherwise that header is captured
/// in <see cref="PendingNonIdatHeader"/> (already consumed from the stream) and reads return 0 (EOF) so
/// the caller can resume its own chunk loop from exactly that point. Mirrors
/// <see cref="PeachImage.Internal.PrefixedStream"/>'s "replay-then-continue" shape.
/// </summary>
internal sealed class PngIdatStream(Stream inner) : Stream
{
    private uint _remainingInCurrentChunk;
    private System.IO.Hashing.Crc32? _crc;
    private bool _reachedEnd;

    /// <summary>The first non-IDAT chunk's header, captured once <see cref="Read(Span{byte})"/> reaches the end of the IDAT run. <see langword="null"/> until then.</summary>
    public PngChunkHeader? PendingNonIdatHeader { get; private set; }

    /// <summary>Begins (or resumes, for the first call) reading from an already-read IDAT chunk header.</summary>
    public void BeginChunk(PngChunkHeader idatHeader)
    {
        _remainingInCurrentChunk = idatHeader.Length;
        _crc = new System.IO.Hashing.Crc32();
        Span<byte> typeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(typeBytes, PngChunkType.Idat.Value);
        _crc.Append(typeBytes);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_reachedEnd)
            {
                return 0;
            }

            if (_remainingInCurrentChunk == 0)
            {
                FinishCurrentChunkCrc();

                var header = PngChunkReader.ReadHeader(inner);
                if (header.Type == PngChunkType.Idat)
                {
                    BeginChunk(header);
                    continue;
                }

                _reachedEnd = true;
                PendingNonIdatHeader = header;
                return 0;
            }

            if (buffer.IsEmpty)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, _remainingInCurrentChunk);
            int actuallyRead = inner.Read(buffer[..toRead]);
            if (actuallyRead == 0)
            {
                throw new PngDecodingException("Unexpected end of stream while reading IDAT chunk data.");
            }

            _crc!.Append(buffer[..actuallyRead]);
            _remainingInCurrentChunk -= (uint)actuallyRead;
            return actuallyRead;
        }
    }

    /// <summary>Drains any leftover bytes of the IDAT run (e.g. deflate padding the compressor never asked for), validating trailing chunk CRCs and capturing <see cref="PendingNonIdatHeader"/> along the way. Call once after the compressor is done.</summary>
    public void DrainToNextChunk()
    {
        Span<byte> discard = stackalloc byte[256];
        while (Read(discard) > 0)
        {
        }
    }

    private void FinishCurrentChunkCrc()
    {
        Span<byte> computedBytes = stackalloc byte[4];
        _crc!.GetCurrentHash(computedBytes);
        uint computed = BinaryPrimitives.ReadUInt32LittleEndian(computedBytes);

        Span<byte> stored = stackalloc byte[4];
        PngChunkReader.ReadExactlyOrThrow(inner, stored);
        uint storedValue = BinaryPrimitives.ReadUInt32BigEndian(stored);

        if (computed != storedValue)
        {
            throw new PngDecodingException("CRC mismatch in 'IDAT' chunk.");
        }
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
