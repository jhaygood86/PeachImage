namespace PeachImage.Formats.Shared.IO;

/// <summary>
/// Reads a length-prefixed payload without trusting the declared length: the buffer is only allocated at the
/// declared size once the stream is known to hold that many bytes, so a tiny file cannot force a huge allocation.
/// </summary>
internal static class BoundedPayloadReader
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>Reads exactly <paramref name="size"/> bytes. Returns <see langword="false"/> when the stream ends first.</summary>
    public static bool TryRead(Stream stream, long size, out byte[] data)
    {
        if (size == 0)
        {
            data = [];
            return true;
        }

        if (stream.CanSeek)
        {
            if (size > stream.Length - stream.Position)
            {
                data = [];
                return false;
            }

            data = new byte[size];
            return stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) == data.Length;
        }

        using var collected = new MemoryStream();
        byte[] block = new byte[(int)Math.Min(size, ChunkSize)];
        long remaining = size;
        while (remaining > 0)
        {
            int read = stream.Read(block, 0, (int)Math.Min(block.Length, remaining));
            if (read == 0)
            {
                data = [];
                return false;
            }

            collected.Write(block, 0, read);
            remaining -= read;
        }

        data = collected.ToArray();
        return true;
    }
}
