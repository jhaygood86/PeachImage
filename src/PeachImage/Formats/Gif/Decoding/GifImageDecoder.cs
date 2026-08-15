using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Orchestrates a full GIF decode: header → a run of extension/image blocks → composited <see cref="GifFrame"/>s. Shared by <see cref="GifDecoder.Decode"/> (stops after one frame) and <see cref="GifDecoder.DecodeAnimation"/> (decodes every frame).</summary>
internal static class GifImageDecoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    public static (List<GifFrame> Frames, int LoopCount) Decode(Stream stream, int maxFrames)
    {
        var header = GifHeaderReader.Read(stream);
        var compositor = new GifFrameCompositor(header.Width, header.Height);
        var frames = new List<GifFrame>();
        int loopCount = 0;
        GifGraphicControlExtension? pendingGce = null;

        try
        {
            while (frames.Count < maxFrames)
            {
                if (!GifStreamHelpers.TryReadByte(stream, out byte blockType) || blockType == Trailer)
                {
                    break;
                }

                if (blockType == ExtensionIntroducer)
                {
                    var (gce, loop) = GifExtensionReader.Read(stream);
                    pendingGce = gce ?? pendingGce;
                    loopCount = loop ?? loopCount;
                    continue;
                }

                if (blockType != ImageSeparator)
                {
                    // Unknown/unsupported block introducer: can't safely resync with the rest of the
                    // stream — stop here with whatever frames were already decoded.
                    break;
                }

                var gceForFrame = pendingGce ?? GifGraphicControlExtension.Default;
                pendingGce = null;

                frames.Add(DecodeFrame(stream, header, gceForFrame, compositor));
            }
        }
        catch
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            throw;
        }

        if (frames.Count == 0)
        {
            throw new GifDecodingException("GIF file contains no image frames.");
        }

        return (frames, loopCount);
    }

    private static GifFrame DecodeFrame(Stream stream, GifHeader header, GifGraphicControlExtension gce, GifFrameCompositor compositor)
    {
        var descriptor = GifImageDescriptorReader.Read(stream);
        byte minCodeSize = GifStreamHelpers.ReadByteOrThrow(stream);
        var (imageData, imageDataLength) = GifSubBlocks.ReadAllImageData(stream);

        try
        {
            byte[] palette = descriptor.LocalColorTable.Length > 0 ? descriptor.LocalColorTable : header.GlobalColorTable;
            if (palette.Length == 0)
            {
                throw new GifDecodingException("GIF frame has no color table (neither local nor global).");
            }

            int pixelCount = descriptor.Width * descriptor.Height;
            var pool = GifBufferPool.Shared;
            byte[] rentedIndices = pool.Rent(pixelCount);
            try
            {
                GifLzwDecoder.DecodeInto(imageData, imageDataLength, minCodeSize, rentedIndices, pixelCount);
                byte[] indices = rentedIndices;

                byte[]? rentedDeinterlaced = null;
                if (descriptor.Interlaced)
                {
                    rentedDeinterlaced = pool.Rent(pixelCount);
                    GifInterlacer.DeinterlaceInto(rentedIndices, rentedDeinterlaced, descriptor.Width, descriptor.Height);
                    indices = rentedDeinterlaced;
                }

                try
                {
                    var frameImage = compositor.DrawFrame(descriptor, indices, palette, gce.TransparentColorIndex, gce.Disposal);
                    return new GifFrame(frameImage, gce.Delay, gce.Disposal);
                }
                finally
                {
                    if (rentedDeinterlaced is not null)
                    {
                        pool.Return(rentedDeinterlaced);
                    }
                }
            }
            finally
            {
                pool.Return(rentedIndices);
            }
        }
        finally
        {
            GifBufferPool.Shared.Return(imageData);
        }
    }
}
