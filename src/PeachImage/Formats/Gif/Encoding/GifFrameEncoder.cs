namespace PeachImage.Formats.Gif.Encoding;

/// <summary>Writes one frame: an optional Graphic Control Extension, Image Descriptor, and LZW-compressed image data.</summary>
internal static class GifFrameEncoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte GraphicControlLabel = 0xF9;
    private const byte ImageSeparator = 0x2C;

    public static void WriteFrame(Stream stream, Image image, byte[] colorTable, int? transparentIndex, TimeSpan duration, GifDisposalMethod disposal, bool dither, int minCodeSize, byte alphaThreshold)
    {
        bool needsGce = transparentIndex.HasValue || duration > TimeSpan.Zero || disposal != GifDisposalMethod.None;
        if (needsGce)
        {
            WriteGraphicControlExtension(stream, transparentIndex, duration, disposal);
        }

        WriteImageDescriptor(stream, image.Width, image.Height);

        byte[] indices = dither
            ? FloydSteinbergDitherer.Map(image, colorTable, transparentIndex, alphaThreshold)
            : GifPaletteMapper.Map(image, colorTable, transparentIndex, alphaThreshold);

        stream.WriteByte((byte)minCodeSize);
        GifLzwEncoder.Encode(stream, indices, minCodeSize);
    }

    private static void WriteGraphicControlExtension(Stream stream, int? transparentIndex, TimeSpan duration, GifDisposalMethod disposal)
    {
        stream.WriteByte(ExtensionIntroducer);
        stream.WriteByte(GraphicControlLabel);
        stream.WriteByte(4);

        int rawDisposal = disposal switch
        {
            GifDisposalMethod.DoNotDispose => 1,
            GifDisposalMethod.RestoreToBackground => 2,
            GifDisposalMethod.RestoreToPrevious => 3,
            _ => 0,
        };

        byte packed = (byte)((rawDisposal << 2) | (transparentIndex.HasValue ? 0x01 : 0x00));
        stream.WriteByte(packed);

        int delayCentiseconds = (int)Math.Clamp(Math.Round(duration.TotalMilliseconds / 10.0), 0, ushort.MaxValue);
        WriteUInt16(stream, (ushort)delayCentiseconds);
        stream.WriteByte((byte)(transparentIndex ?? 0));
        stream.WriteByte(0);
    }

    private static void WriteImageDescriptor(Stream stream, int width, int height)
    {
        stream.WriteByte(ImageSeparator);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, (ushort)width);
        WriteUInt16(stream, (ushort)height);
        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
