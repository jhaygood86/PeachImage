using PeachImage.Formats.Bmp.Internal;

namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>Orchestrates a full BMP decode: header → palette → pixel data → <see cref="Image"/>.</summary>
internal static class BmpImageDecoder
{
    public static Image Decode(Stream stream)
    {
        var header = BmpHeaderReader.Read(stream);

        bool isIndexed = header.BitCount <= 8;
        byte[] palette = isIndexed ? BmpPalette.Read(stream, header) : [];

        long paletteBytesRead = isIndexed ? (long)(palette.Length / 3) * header.PaletteEntrySize : 0;
        BmpStreamHelpers.SkipForward(stream, header.PixelDataOffset - header.HeaderBytesConsumed - paletteBytesRead);

        bool hasAlpha = header.HasAlphaMask && header.BitCount is 16 or 32;
        var pixelFormat = hasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        var image = Image.Create(header.Width, header.Height, pixelFormat);

        if (header.Compression is BmpCompression.Rle8 or BmpCompression.Rle4)
        {
            DecodeRlePixels(stream, header, palette, image);
        }
        else
        {
            DecodeUncompressedPixels(stream, header, palette, isIndexed, hasAlpha, image);
        }

        return image;
    }

    private static void DecodeUncompressedPixels(Stream stream, BmpHeader header, byte[] palette, bool isIndexed, bool hasAlpha, Image image)
    {
        int paddedRowBytes = BmpRowUnpacker.GetPaddedRowByteCount(header.Width, header.BitCount);
        Span<byte> rowBuffer = paddedRowBytes is > 0 and <= 4096 ? stackalloc byte[paddedRowBytes] : new byte[paddedRowBytes];
        byte[] indexBuffer = isIndexed ? new byte[header.Width] : [];

        for (int sourceRow = 0; sourceRow < header.Height; sourceRow++)
        {
            BmpStreamHelpers.ReadExactlyOrThrow(stream, rowBuffer);

            int destRowIndex = header.TopDown ? sourceRow : header.Height - 1 - sourceRow;
            var destRow = image.GetRowSpan(destRowIndex);

            if (isIndexed)
            {
                BmpRowUnpacker.UnpackBitsToIndices(rowBuffer, header.BitCount, indexBuffer);
                BmpRowUnpacker.ResolveIndexRow(indexBuffer, palette, destRow);
            }
            else
            {
                BmpRowUnpacker.UnpackDirectColorRow(rowBuffer, header.BitCount, header, hasAlpha, destRow);
            }
        }
    }

    private static void DecodeRlePixels(Stream stream, BmpHeader header, byte[] palette, Image image)
    {
        byte[] compressed = ReadRemainingData(stream, header.DeclaredImageSize);
        byte[] indices = header.Compression == BmpCompression.Rle8
            ? BmpRleDecoder.DecodeRle8(compressed, header.Width, header.Height)
            : BmpRleDecoder.DecodeRle4(compressed, header.Width, header.Height);

        // RLE-compressed BMPs are always bottom-up (rejected as invalid otherwise during header parsing):
        // index buffer row 0 is the image's bottom row.
        for (int bufferRow = 0; bufferRow < header.Height; bufferRow++)
        {
            int destRowIndex = header.Height - 1 - bufferRow;
            var destRow = image.GetRowSpan(destRowIndex);
            var indexRow = indices.AsSpan(bufferRow * header.Width, header.Width);
            BmpRowUnpacker.ResolveIndexRow(indexRow, palette, destRow);
        }
    }

    private static byte[] ReadRemainingData(Stream stream, uint declaredSize)
    {
        if (declaredSize > 0)
        {
            if (declaredSize > BmpDecodingLimits.MaxDeclaredImageSizeBytes)
            {
                throw new BmpDecodingException($"BMP declared RLE image data size ({declaredSize} bytes) exceeds the maximum supported size.");
            }

            if (stream.CanSeek)
            {
                long remaining = stream.Length - stream.Position;
                if (declaredSize > remaining)
                {
                    throw new BmpDecodingException($"BMP declared RLE image data size ({declaredSize} bytes) exceeds the {remaining} bytes remaining in the stream.");
                }
            }

            var buffer = new byte[declaredSize];
            BmpStreamHelpers.ReadExactlyOrThrow(stream, buffer);
            return buffer;
        }

        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }
}
