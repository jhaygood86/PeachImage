using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reads GIF extension blocks (0x21 introducer + label) that follow the Global Color Table.</summary>
internal static class GifExtensionReader
{
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationLabel = 0xFF;

    /// <summary>Reads and dispatches one extension block (the 0x21 introducer has already been consumed). Returns an updated pending Graphic Control Extension and loop count, if this block provided one.</summary>
    public static (GifGraphicControlExtension? Gce, int? LoopCount) Read(Stream stream) => Read(stream, out _);

    /// <summary>As <see cref="Read(Stream)"/>, also returning the XMP packet when the block is an <c>XMP DataXMP</c> application extension.</summary>
    public static (GifGraphicControlExtension? Gce, int? LoopCount) Read(Stream stream, out byte[]? xmp)
    {
        xmp = null;
        byte label = GifStreamHelpers.ReadByteOrThrow(stream);

        switch (label)
        {
            case GraphicControlLabel:
                return (ReadGraphicControlExtension(stream), null);
            case ApplicationLabel:
                return (null, ReadApplicationExtension(stream, out xmp));
            default:
                // Comment (0xFE), Plain Text (0x01), and any other/unknown extension: not needed for
                // decoding a visual frame — skip its sub-blocks. Plain Text also carries a fixed 12-byte
                // header sub-block before its text sub-blocks; GifSubBlocks.SkipAll handles it uniformly
                // since that header block is itself just the first size-prefixed sub-block.
                GifSubBlocks.SkipAll(stream);
                return (null, null);
        }
    }

    private static GifGraphicControlExtension ReadGraphicControlExtension(Stream stream)
    {
        var (data, length) = GifSubBlocks.ReadAll(stream);
        try
        {
            if (length < 4)
            {
                // Malformed/truncated GCE: treat as "no control info" rather than throwing, so a single bad
                // extension doesn't take down an otherwise-decodable animation.
                return GifGraphicControlExtension.Default;
            }

            byte packed = data[0];
            int rawDisposal = (packed >> 2) & 0x07;
            bool transparentFlag = (packed & 0x01) != 0;
            int delayCentiseconds = data[1] | (data[2] << 8);
            int transparentIndex = data[3];

            var disposal = rawDisposal switch
            {
                1 => GifDisposalMethod.DoNotDispose,
                2 => GifDisposalMethod.RestoreToBackground,
                3 => GifDisposalMethod.RestoreToPrevious,
                _ => GifDisposalMethod.None,
            };

            return new GifGraphicControlExtension
            {
                Disposal = disposal,
                TransparentColorIndex = transparentFlag ? transparentIndex : null,
                Delay = TimeSpan.FromMilliseconds(delayCentiseconds * 10),
            };
        }
        finally
        {
            GifBufferPool.Shared.Return(data);
        }
    }

    private static int? ReadApplicationExtension(Stream stream, out byte[]? xmp)
    {
        xmp = null;

        // The first sub-block is the 11-byte identifier + authentication code (an 8-byte application name and a 3-byte code).
        byte first = GifStreamHelpers.ReadByteOrThrow(stream);
        if (first == 0)
        {
            return null;
        }

        byte[] head = new byte[first];
        GifStreamHelpers.ReadExactlyOrThrow(stream, head);

        if (head.AsSpan().SequenceEqual("XMP DataXMP"u8))
        {
            xmp = ReadXmpPacket(stream);
            return null;
        }

        var (rest, restLength) = GifSubBlocks.ReadAll(stream);
        try
        {
            // NETSCAPE2.0's single application data sub-block is: byte 0 = 1 (sub-block ID), bytes 1-2 = loop count (LE, 0 = infinite).
            if (first != 11 || restLength < 3 || rest[0] != 1 || !head.AsSpan(0, 8).SequenceEqual("NETSCAPE"u8))
            {
                return null;
            }

            return rest[1] | (rest[2] << 8);
        }
        finally
        {
            GifBufferPool.Shared.Return(rest);
        }
    }

    /// <summary>
    /// Reads an XMP application extension's data. The packet is stored raw, not as size-prefixed sub-blocks: the extension ends with a
    /// 257-byte "magic trailer" (<c>0x01, 0xFF, 0xFE ... 0x01, 0x00</c>, then the block terminator) built so that a reader walking it as
    /// ordinary sub-blocks lands exactly on the terminator. So the bytes are collected as such a reader sees them, size bytes included,
    /// and the trailer is cut off the end to leave the packet.
    /// </summary>
    internal static byte[] ReadXmpPacket(Stream stream)
    {
        using var raw = new MemoryStream();
        Span<byte> chunk = stackalloc byte[byte.MaxValue];
        while (GifStreamHelpers.TryReadByte(stream, out byte size) && size != 0)
        {
            if (raw.Length + 1 + size > GifDecodingLimits.MaxExtensionBytes)
            {
                throw new GifDecodingException("GIF XMP extension exceeds the maximum allowed size.");
            }

            raw.WriteByte(size);
            var slice = chunk[..size];
            GifStreamHelpers.ReadExactlyOrThrow(stream, slice);
            raw.Write(slice);
        }

        byte[] bytes = raw.ToArray();
        return HasMagicTrailer(bytes) ? bytes[..^MagicTrailerLength] : bytes;
    }

    /// <summary>The length of the XMP magic trailer without its final block terminator.</summary>
    internal const int MagicTrailerLength = 257;

    private static bool HasMagicTrailer(byte[] bytes)
    {
        if (bytes.Length < MagicTrailerLength || bytes[^MagicTrailerLength] != 0x01)
        {
            return false;
        }

        for (int i = 1; i < MagicTrailerLength; i++)
        {
            if (bytes[bytes.Length - MagicTrailerLength + i] != (byte)(256 - i))
            {
                return false;
            }
        }

        return true;
    }
}
