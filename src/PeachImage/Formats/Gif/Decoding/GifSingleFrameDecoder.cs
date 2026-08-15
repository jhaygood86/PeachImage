using System.Buffers;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Decodes just the first frame directly into its final <see cref="Image"/> buffer — no persistent RGBA
/// working canvas, no "decode to Rgba32 then downgrade to Rgb24" copy. <see cref="GifImageDecoder"/>'s general
/// multi-frame <see cref="GifFrameCompositor"/> path is unnecessary here: frame 1 is always drawn onto a
/// blank canvas (no prior frame to dispose, no snapshot to restore), so this resolves indices straight into
/// the destination image's own pixel buffer. Used by <see cref="GifDecoder.Decode"/>, the common case and the
/// one perf-sensitive enough to warrant a dedicated path (see the project's decode benchmarks).
/// </summary>
internal static class GifSingleFrameDecoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    public static Image Decode(Stream stream)
    {
        var header = GifHeaderReader.Read(stream);
        GifGraphicControlExtension? pendingGce = null;

        while (true)
        {
            if (!GifStreamHelpers.TryReadByte(stream, out byte blockType) || blockType == Trailer)
            {
                throw new GifDecodingException("GIF file contains no image frames.");
            }

            if (blockType == ExtensionIntroducer)
            {
                var (gce, _) = GifExtensionReader.Read(stream);
                pendingGce = gce ?? pendingGce;
                continue;
            }

            if (blockType != ImageSeparator)
            {
                throw new GifDecodingException("GIF file contains no image frames.");
            }

            return DecodeFrame(stream, header, pendingGce ?? GifGraphicControlExtension.Default);
        }
    }

    private static Image DecodeFrame(Stream stream, GifHeader header, GifGraphicControlExtension gce)
    {
        var descriptor = GifImageDescriptorReader.Read(stream);
        byte minCodeSize = GifStreamHelpers.ReadByteOrThrow(stream);
        byte[] imageData = GifSubBlocks.ReadAllImageData(stream);

        byte[] palette = descriptor.LocalColorTable.Length > 0 ? descriptor.LocalColorTable : header.GlobalColorTable;
        if (palette.Length == 0)
        {
            throw new GifDecodingException("GIF frame has no color table (neither local nor global).");
        }

        int pixelCount = descriptor.Width * descriptor.Height;

        // The indices buffer is a pure intermediate — resolved into the final Image below and then
        // discarded — so it's array-pool-rented rather than GC-allocated, avoiding a full-frame-sized
        // (often large-object-heap) allocation on every decode.
        byte[] rentedIndices = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            GifLzwDecoder.DecodeInto(imageData, minCodeSize, rentedIndices, pixelCount);
            byte[] indices = rentedIndices;

            byte[]? rentedDeinterlaced = null;
            if (descriptor.Interlaced)
            {
                rentedDeinterlaced = ArrayPool<byte>.Shared.Rent(pixelCount);
                GifInterlacer.DeinterlaceInto(rentedIndices, rentedDeinterlaced, descriptor.Width, descriptor.Height);
                indices = rentedDeinterlaced;
            }

            bool hasAlpha = gce.TransparentColorIndex.HasValue;
            var image = Image.Create(header.Width, header.Height, hasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24);
            try
            {
                ResolveIndices(image, descriptor, indices, palette, gce.TransparentColorIndex, hasAlpha);
            }
            catch
            {
                image.Dispose();
                throw;
            }
            finally
            {
                if (rentedDeinterlaced is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedDeinterlaced);
                }
            }

            return image;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedIndices);
        }
    }

    private static void ResolveIndices(Image image, GifImageDescriptor descriptor, byte[] indices, byte[] palette, int? transparentIndex, bool hasAlpha)
    {
        int paletteEntryCount = palette.Length / 3;
        int bytesPerPixel = hasAlpha ? 4 : 3;

        for (int y = 0; y < descriptor.Height; y++)
        {
            int canvasY = descriptor.Top + y;
            if ((uint)canvasY >= (uint)image.Height)
            {
                continue;
            }

            var destRow = image.GetRowSpan(canvasY);

            for (int x = 0; x < descriptor.Width; x++)
            {
                int canvasX = descriptor.Left + x;
                if ((uint)canvasX >= (uint)image.Width)
                {
                    continue;
                }

                int index = indices[(y * descriptor.Width) + x];
                if (transparentIndex.HasValue && index == transparentIndex.Value)
                {
                    continue;
                }

                if ((uint)index >= (uint)paletteEntryCount)
                {
                    continue;
                }

                int destOffset = canvasX * bytesPerPixel;
                int paletteOffset = index * 3;
                destRow[destOffset] = palette[paletteOffset];
                destRow[destOffset + 1] = palette[paletteOffset + 1];
                destRow[destOffset + 2] = palette[paletteOffset + 2];
                if (hasAlpha)
                {
                    destRow[destOffset + 3] = 255;
                }
            }
        }
    }
}
