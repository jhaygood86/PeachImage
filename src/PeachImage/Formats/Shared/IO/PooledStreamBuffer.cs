using System.Buffers;

namespace PeachImage.Formats.Shared.IO;

/// <summary>
/// The remaining contents of a <see cref="Stream"/>, read into a pooled buffer. Seekable streams are sized
/// exactly up front; others grow by doubling. Dispose returns the buffer to the pool.
/// </summary>
internal struct PooledStreamBuffer : IDisposable
{
    private byte[]? _array;

    private PooledStreamBuffer(byte[] array, int length)
    {
        _array = array;
        Length = length;
    }

    /// <summary>The number of valid bytes.</summary>
    public int Length { get; }

    /// <summary>The bytes read.</summary>
    public readonly ReadOnlySpan<byte> Span => _array.AsSpan(0, Length);

    /// <summary>The bytes read, as memory (valid until <see cref="Dispose"/>).</summary>
    public readonly ReadOnlyMemory<byte> ToMemory() => _array.AsMemory(0, Length);

    /// <summary>Reads <paramref name="stream"/> to its end. <paramref name="tooLarge"/> is set, and an empty buffer returned, when it exceeds <paramref name="maxLength"/>.</summary>
    public static PooledStreamBuffer Read(Stream stream, long maxLength, out bool tooLarge)
    {
        tooLarge = false;
        int capacity = 4096;
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining > maxLength)
            {
                tooLarge = true;
                return default;
            }

            capacity = (int)Math.Max(remaining, 1);
        }

        byte[] array = ArrayPool<byte>.Shared.Rent(capacity);
        int length = 0;
        while (true)
        {
            if (length == array.Length)
            {
                if (length >= maxLength)
                {
                    ArrayPool<byte>.Shared.Return(array);
                    tooLarge = true;
                    return default;
                }

                byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(maxLength, (long)array.Length * 2));
                array.AsSpan(0, length).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(array);
                array = bigger;
            }

            int read = stream.Read(array, length, array.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return new PooledStreamBuffer(array, length);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_array is not null)
        {
            ArrayPool<byte>.Shared.Return(_array);
            _array = null;
        }
    }
}
