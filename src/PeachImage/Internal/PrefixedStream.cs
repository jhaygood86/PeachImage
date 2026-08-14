namespace PeachImage.Internal;

/// <summary>
/// A forward-only, non-seekable stream that first replays a buffered prefix (bytes already consumed
/// while sniffing a format's magic header) before continuing to read from the underlying stream.
/// Used so non-seekable input streams can still be sniffed by <see cref="ImageFormatManager"/>.
/// </summary>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

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
        if (_prefixPosition < prefix.Length)
        {
            var remaining = prefix.AsSpan(_prefixPosition);
            int toCopy = Math.Min(remaining.Length, buffer.Length);
            remaining[..toCopy].CopyTo(buffer);
            _prefixPosition += toCopy;
            return toCopy;
        }

        return inner.Read(buffer);
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
