namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reads GIF extension blocks (0x21 introducer + label) that follow the Global Color Table.</summary>
internal static class GifExtensionReader
{
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationLabel = 0xFF;

    /// <summary>Reads and dispatches one extension block (the 0x21 introducer has already been consumed). Returns an updated pending Graphic Control Extension and loop count, if this block provided one.</summary>
    public static (GifGraphicControlExtension? Gce, int? LoopCount) Read(Stream stream)
    {
        byte label = GifStreamHelpers.ReadByteOrThrow(stream);

        switch (label)
        {
            case GraphicControlLabel:
                return (ReadGraphicControlExtension(stream), null);
            case ApplicationLabel:
                return (null, ReadApplicationExtension(stream));
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
        byte[] data = GifSubBlocks.ReadAll(stream);
        if (data.Length < 4)
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

    private static int? ReadApplicationExtension(Stream stream)
    {
        byte[] data = GifSubBlocks.ReadAll(stream);

        // Application Extension layout: 8-byte Application Identifier + 3-byte Authentication Code (both
        // folded into the leading sub-blocks by ReadAll), followed by application data sub-blocks (also
        // folded in). NETSCAPE2.0's single application data sub-block is: byte 0 = 1 (sub-block ID),
        // bytes 1-2 = loop count (LE, 0 = infinite).
        if (data.Length < 11)
        {
            return null;
        }

        string applicationId = System.Text.Encoding.ASCII.GetString(data, 0, 8);
        if (applicationId != "NETSCAPE")
        {
            return null;
        }

        if (data.Length < 14 || data[11] != 1)
        {
            return null;
        }

        return data[12] | (data[13] << 8);
    }
}
