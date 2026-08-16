namespace PeachImage.Formats.Gif.Encoding;

/// <summary>Orchestrates a full GIF encode: header, Logical Screen Descriptor, Global Color Table, optional NETSCAPE2.0 loop extension, frames, trailer.</summary>
internal static class GifImageEncoder
{
    private const byte Trailer = 0x3B;
    private const byte AlphaThreshold = 128;

    public static void EncodeStatic(Image image, Stream stream, GifEncoderOptions options)
    {
        EncodeFrames(stream, [(image, TimeSpan.Zero, GifDisposalMethod.None)], loopCount: null, options);
    }

    public static void EncodeAnimation(AnimatedImage image, Stream stream, GifEncoderOptions options)
    {
        var frames = new List<(Image, TimeSpan, GifDisposalMethod)>(image.Frames.Count);
        foreach (var frame in image.Frames)
        {
            frames.Add((frame.Image, frame.Duration, ToGifDisposalMethod(frame.Disposal)));
        }

        EncodeFrames(stream, frames, image.LoopCount, options);
    }

    private static GifDisposalMethod ToGifDisposalMethod(FrameDisposalMethod disposal) => disposal switch
    {
        FrameDisposalMethod.DoNotDispose => GifDisposalMethod.DoNotDispose,
        FrameDisposalMethod.RestoreToBackground => GifDisposalMethod.RestoreToBackground,
        FrameDisposalMethod.RestoreToPrevious => GifDisposalMethod.RestoreToPrevious,
        _ => GifDisposalMethod.None,
    };

    private static void EncodeFrames(Stream stream, List<(Image Image, TimeSpan Duration, GifDisposalMethod Disposal)> frames, int? loopCount, GifEncoderOptions options)
    {
        if (frames.Count == 0)
        {
            throw new GifEncodingException("Cannot encode a GIF with no frames.");
        }

        int width = frames[0].Image.Width;
        int height = frames[0].Image.Height;

        foreach (var frame in frames)
        {
            if (frame.Image.Width != width || frame.Image.Height != height)
            {
                throw new GifEncodingException("All frames of an animated GIF must share the same dimensions.");
            }

            if (frame.Image.PixelFormat is not (PixelFormat.Rgb24 or PixelFormat.Rgba32))
            {
                throw new GifEncodingException($"Cannot encode pixel format {frame.Image.PixelFormat} as GIF.");
            }
        }

        bool hasTransparency = false;
        foreach (var frame in frames)
        {
            if (frame.Image.PixelFormat == PixelFormat.Rgba32 && HasTransparentPixel(frame.Image, AlphaThreshold))
            {
                hasTransparency = true;
                break;
            }
        }

        int maxColors = Math.Clamp(options.MaxColors, 2, 256);
        int quantizeTarget = Math.Max(1, hasTransparency ? maxColors - 1 : maxColors);

        var histogram = new GifColorHistogram();
        foreach (var frame in frames)
        {
            histogram.AddOpaquePixels(frame.Image, AlphaThreshold);
        }

        byte[] palette = MedianCutQuantizer.BuildPalette(histogram, quantizeTarget);

        int? transparentIndex = null;
        int usedColorCount = palette.Length / 3;
        if (hasTransparency)
        {
            transparentIndex = usedColorCount;
            usedColorCount++;
        }

        int colorTableSize = GifPaletteSizing.ColorTableSizeFor(usedColorCount);
        int minCodeSize = GifPaletteSizing.MinCodeSizeFor(colorTableSize);

        byte[] colorTable = new byte[colorTableSize * 3];
        palette.CopyTo(colorTable, 0);

        WriteHeader(stream, width, height, colorTable);

        if (loopCount is { } loops && frames.Count > 1)
        {
            WriteNetscapeLoopExtension(stream, loops);
        }

        // Built once and reused across every pixel of every frame — the k-d tree's O(n log n) build cost is
        // amortized against however many pixels this encode ends up mapping through it.
        var paletteTree = new GifPaletteKdTree(colorTable);

        foreach (var frame in frames)
        {
            GifFrameEncoder.WriteFrame(stream, frame.Image, paletteTree, transparentIndex, frame.Duration, frame.Disposal, options.Dither, minCodeSize, AlphaThreshold);
        }

        stream.WriteByte(Trailer);
    }

    private static bool HasTransparentPixel(Image image, byte alphaThreshold)
    {
        var pixels = image.GetPixelSpan();
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < alphaThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteHeader(Stream stream, int width, int height, byte[] colorTable)
    {
        stream.Write("GIF89a"u8);
        WriteUInt16(stream, (ushort)width);
        WriteUInt16(stream, (ushort)height);

        int colorTableSize = colorTable.Length / 3;
        byte packed = (byte)(0x80 | (0x07 << 4) | GifPaletteSizing.SizeFieldFor(colorTableSize));
        stream.WriteByte(packed);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.Write(colorTable);
    }

    private static void WriteNetscapeLoopExtension(Stream stream, int loopCount)
    {
        stream.WriteByte(0x21);
        stream.WriteByte(0xFF);
        stream.WriteByte(11);
        stream.Write("NETSCAPE2.0"u8);
        stream.WriteByte(3);
        stream.WriteByte(1);
        WriteUInt16(stream, (ushort)Math.Clamp(loopCount, 0, ushort.MaxValue));
        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
