namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Decompresses TIFF's PackBits (Compression=32773) byte-oriented run-length scheme: a signed control byte
/// <c>n</c>, followed by either <c>n+1</c> literal bytes (<c>n &gt;= 0</c>) or one byte repeated
/// <c>1-n</c> times (<c>n &lt; 0</c>, <c>n != -128</c>); <c>n == -128</c> is a no-op. Never throws on
/// truncated/malformed input — stops early and leaves the rest of the output span as whatever the caller
/// pre-filled it with, mirroring <c>Bmp.Decoding.BmpRleDecoder</c>'s/<c>Gif.Decoding.GifLzwDecoder</c>'s
/// defensive convention.
/// </summary>
internal static class TiffPackBitsDecoder
{
    public static void Decode(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        int src = 0;
        int dst = 0;

        while (src < compressed.Length && dst < output.Length)
        {
            sbyte control = unchecked((sbyte)compressed[src++]);

            if (control >= 0)
            {
                int count = control + 1;
                count = Math.Min(count, compressed.Length - src);
                count = Math.Min(count, output.Length - dst);

                compressed.Slice(src, count).CopyTo(output.Slice(dst, count));
                src += count;
                dst += count;
            }
            else if (control != -128)
            {
                if (src >= compressed.Length)
                {
                    return;
                }

                byte value = compressed[src++];
                int count = Math.Min(1 - control, output.Length - dst);
                output.Slice(dst, count).Fill(value);
                dst += count;
            }

            // control == -128: no-op, just consume the control byte.
        }
    }
}
