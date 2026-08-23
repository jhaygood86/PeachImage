using PeachImage.Formats.Tiff.Internal;

namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>Small stream-reading helpers for TIFF decode internals.</summary>
internal static class TiffStreamHelpers
{
    /// <summary>
    /// Buffers <paramref name="stream"/> in full into a <c>byte[]</c>. TIFF tag values and strip data are
    /// addressed by absolute file offset and jump around arbitrarily, so a streaming reader would end up
    /// buffering anyway — mirrors Avif's <c>AvifContainerReader.BufferStream</c> for the same reason.
    /// </summary>
    public static byte[] BufferStream(Stream stream)
    {
        if (stream.CanSeek)
        {
            long declaredLength = stream.Length - stream.Position;
            if (declaredLength > TiffDecodingLimits.MaxFileSize)
            {
                throw new TiffDecodingException($"TIFF file size ({declaredLength} bytes) exceeds the supported maximum of {TiffDecodingLimits.MaxFileSize} bytes.");
            }
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > TiffDecodingLimits.MaxFileSize)
            {
                throw new TiffDecodingException($"TIFF file size exceeds the supported maximum of {TiffDecodingLimits.MaxFileSize} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
