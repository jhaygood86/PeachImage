using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Orchestrates a GIF decode: header → a run of extension/image blocks → composited <see cref="GifFrame"/>s.
/// Split into an eager header read (<see cref="ReadHeader"/>), an eager prelude read (<see cref="ReadPrelude"/>,
/// which captures whatever precedes frame 1 — its Graphic Control Extension and the NETSCAPE2.0 loop count, if
/// present), and a lazy per-frame iterator (<see cref="DecodeFrames"/>) that decodes one frame at a time as the
/// caller enumerates. <see cref="GifDecoder.Decode"/> uses only the header (via the separate, single-frame-optimized
/// <see cref="GifSingleFrameDecoder"/>); <see cref="GifDecoder.DecodeAnimation"/> uses all three.
/// </summary>
internal static class GifImageDecoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    /// <summary>Whatever precedes frame 1: its Graphic Control Extension (if any) and the loop count seen so far.</summary>
    public readonly record struct GifPrelude(GifGraphicControlExtension? FirstFrameGce, int LoopCount);

    public static GifHeader ReadHeader(Stream stream) => GifHeaderReader.Read(stream);

    /// <summary>
    /// Reads blocks up to and including the first Image Separator, capturing whatever Graphic Control
    /// Extension applies to frame 1 and the NETSCAPE2.0 loop count if it appears before frame 1 — the
    /// overwhelmingly common real-world layout (this library's own encoder always writes it first). A loop
    /// extension appearing after frame 1 is non-conformant and not detected here; <see cref="GifDecoder.DecodeAnimation"/>
    /// documents this as an accepted limitation. Throws if the stream ends, or an unrecognized block is hit,
    /// before any Image Separator is found.
    /// </summary>
    public static GifPrelude ReadPrelude(Stream stream)
    {
        GifGraphicControlExtension? pendingGce = null;
        int loopCount = 0;

        while (true)
        {
            if (!GifStreamHelpers.TryReadByte(stream, out byte blockType) || blockType == Trailer)
            {
                throw new GifDecodingException("GIF file contains no image frames.");
            }

            if (blockType == ExtensionIntroducer)
            {
                var (gce, loop) = GifExtensionReader.Read(stream);
                pendingGce = gce ?? pendingGce;
                if (loop is { } l)
                {
                    loopCount = l;
                }

                continue;
            }

            if (blockType != ImageSeparator)
            {
                throw new GifDecodingException("GIF file contains no image frames.");
            }

            return new GifPrelude(pendingGce, loopCount);
        }
    }

    /// <summary>
    /// Lazily decodes and yields one frame at a time, starting with frame 1 (whose Image Separator was
    /// already consumed by <see cref="ReadPrelude"/>) and continuing until the Trailer, end of stream,
    /// <paramref name="maxFrames"/>, or an unrecognized block is hit. Enforces <paramref name="maxCumulativeCanvasBytes"/>
    /// before each frame — a decode-time CPU/GC-pressure guard against a pathological frame count (see
    /// <see cref="GifDecodingLimits.MaxCumulativeCanvasBytes"/>'s doc comment for why this no longer bounds
    /// *retained* memory the way it used to, now that frames aren't all kept resident at once).
    /// </summary>
    public static IEnumerable<GifFrame> DecodeFrames(Stream stream, GifHeader header, GifPrelude prelude, int maxFrames, long maxCumulativeCanvasBytes)
    {
        var compositor = new GifFrameCompositor(header.Width, header.Height);
        long canvasBytes = (long)header.Width * header.Height * 4;
        GifGraphicControlExtension? pendingGce = prelude.FirstFrameGce;
        int frameCount = 0;
        bool haveImageSeparator = true;

        while (frameCount < maxFrames)
        {
            if (!haveImageSeparator)
            {
                if (!GifStreamHelpers.TryReadByte(stream, out byte blockType) || blockType == Trailer)
                {
                    yield break;
                }

                if (blockType == ExtensionIntroducer)
                {
                    var (gce, _) = GifExtensionReader.Read(stream);
                    pendingGce = gce ?? pendingGce;
                    continue;
                }

                if (blockType != ImageSeparator)
                {
                    // Unknown/unsupported block introducer: can't safely resync with the rest of the
                    // stream — stop here with whatever frames were already decoded.
                    yield break;
                }
            }

            haveImageSeparator = false;

            long cumulativeCanvasBytes = canvasBytes * (frameCount + 1);
            if (cumulativeCanvasBytes > maxCumulativeCanvasBytes)
            {
                throw new GifDecodingException($"GIF animation's cumulative frame-canvas memory ({cumulativeCanvasBytes:N0} bytes across {frameCount + 1} frames) exceeds the {maxCumulativeCanvasBytes:N0}-byte limit.");
            }

            var gceForFrame = pendingGce ?? GifGraphicControlExtension.Default;
            pendingGce = null;
            frameCount++;
            yield return DecodeFrame(stream, header, gceForFrame, compositor);
        }
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
