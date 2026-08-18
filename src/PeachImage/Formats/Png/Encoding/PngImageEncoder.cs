using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>Top-level PNG encode orchestrator: writes IHDR/PLTE/ancillary chunks, then a per-pass (Adam7-aware) filtered/deflated scanline stream.</summary>
internal static class PngImageEncoder
{
    public static void Encode(Image image, Stream stream, PngEncoderOptions options)
    {
        var indexedPlan = PngIndexedPaletteBuilder.TryBuild(image, options);

        PngColorType colorType;
        byte bitDepth;
        int channels;
        bool is16;

        if (indexedPlan is { } plan)
        {
            colorType = PngColorType.Palette;
            bitDepth = plan.BitDepth;
            channels = 1;
            is16 = false;
        }
        else
        {
            (colorType, bitDepth) = PngHeaderWriter.ChooseColorType(image.PixelFormat);
            is16 = bitDepth == 16;
            channels = image.PixelFormat.GetChannelCount();
        }

        var header = new PngHeader(image.Width, image.Height, bitDepth, colorType, 0, 0, 0);
        int bitsPerPixel = header.BitsPerPixel;
        int filterBytesPerPixel = header.FilterBytesPerPixel;

        PngChunkWriter.WriteSignature(stream);
        PngHeaderWriter.WriteIhdr(stream, image.Width, image.Height, colorType, bitDepth, options.Interlace);

        if (options.IncludeMetadata)
        {
            PngAncillaryChunkWriter.WriteColorProfileChunks(stream, image.Metadata);
        }

        if (indexedPlan is { } indexed)
        {
            WritePlte(stream, indexed);
            if (indexed.TransparentIndex is { } transparentIndex)
            {
                WritePaletteTrns(stream, transparentIndex);
            }
        }
        else
        {
            bool sourceHasAlpha = image.PixelFormat is PixelFormat.Rgba32 or PixelFormat.Rgba64;
            if (options.TransparentColor is { } key && !sourceHasAlpha)
            {
                WriteTrns(stream, colorType, key, is16);
            }
        }

        if (options.IncludeMetadata)
        {
            PngAncillaryChunkWriter.WriteRemainingChunks(stream, image.Metadata);
        }

        ReadOnlySpan<Adam7Pass> passes = options.Interlace ? Adam7.Passes : [new Adam7Pass(0, 0, 1, 1)];

        var idatWriter = new ChunkedIdatWriterStream(stream);
        var compressionLevel = MapCompressionLevel(options.CompressionLevel);

        using (var zlib = new ZLibStream(idatWriter, compressionLevel, leaveOpen: true))
        {
            foreach (var pass in passes)
            {
                var (passWidth, passHeight) = options.Interlace
                    ? Adam7.GetPassDimensions(image.Width, image.Height, pass)
                    : (image.Width, image.Height);

                if (passWidth == 0 || passHeight == 0)
                {
                    continue;
                }

                int rawRowBytes = PngHeader.BytesPerScanline(passWidth, bitsPerPixel);
                var rawRowBuffer = ArrayPool<byte>.Shared.Rent(rawRowBytes);
                var previousRawRowBuffer = ArrayPool<byte>.Shared.Rent(rawRowBytes);
                var filteredRowBuffer = ArrayPool<byte>.Shared.Rent(rawRowBytes);
                var scratchBuffer = ArrayPool<byte>.Shared.Rent(rawRowBytes);
                var indexRowBuffer = indexedPlan is not null ? ArrayPool<byte>.Shared.Rent(passWidth) : null;
                try
                {
                    var rawRow = rawRowBuffer.AsSpan(0, rawRowBytes);
                    var previousRawRow = previousRawRowBuffer.AsSpan(0, rawRowBytes);
                    var filteredRow = filteredRowBuffer.AsSpan(0, rawRowBytes);
                    var scratch = scratchBuffer.AsSpan(0, rawRowBytes);
                    Span<byte> indexRow = indexRowBuffer is not null ? indexRowBuffer.AsSpan(0, passWidth) : default;
                    bool firstRow = true;

                    for (int y = 0; y < passHeight; y++)
                    {
                        int srcY = pass.StartY + (y * pass.StrideY);

                        if (indexedPlan is { } activePlan)
                        {
                            BuildIndexedRawRow(activePlan, image.Width, srcY, pass, passWidth, bitDepth, indexRow, rawRow);
                        }
                        else
                        {
                            BuildRawRow(image, srcY, pass, passWidth, filterBytesPerPixel, channels, is16, rawRow);
                        }

                        var previous = firstRow ? ReadOnlySpan<byte>.Empty : previousRawRow;
                        var filterType = PngRowFilterSelector.FilterRow(rawRow, previous, filterBytesPerPixel, options.FilterStrategy, scratch, filteredRow);
                        zlib.WriteByte((byte)filterType);
                        zlib.Write(filteredRow);

                        var swapRow = rawRow;
                        rawRow = previousRawRow;
                        previousRawRow = swapRow;
                        (rawRowBuffer, previousRawRowBuffer) = (previousRawRowBuffer, rawRowBuffer);
                        firstRow = false;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rawRowBuffer);
                    ArrayPool<byte>.Shared.Return(previousRawRowBuffer);
                    ArrayPool<byte>.Shared.Return(filteredRowBuffer);
                    ArrayPool<byte>.Shared.Return(scratchBuffer);
                    if (indexRowBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(indexRowBuffer);
                    }
                }
            }
        }

        idatWriter.FinishFinalChunk();
        PngChunkWriter.WriteChunk(stream, PngChunkType.Iend, ReadOnlySpan<byte>.Empty);
    }

    private static void BuildIndexedRawRow(PngIndexedPaletteBuilder.Plan plan, int imageWidth, int srcY, in Adam7Pass pass, int passWidth, int bitDepth, Span<byte> indexRow, Span<byte> raw)
    {
        var indices = plan.Indices;
        int rowOffset = srcY * imageWidth;

        if (pass.StrideX == 1)
        {
            indices.AsSpan(rowOffset + pass.StartX, passWidth).CopyTo(indexRow);
        }
        else
        {
            for (int x = 0; x < passWidth; x++)
            {
                int srcX = pass.StartX + (x * pass.StrideX);
                indexRow[x] = indices[rowOffset + srcX];
            }
        }

        PngIndexedBitPacker.PackRow(indexRow, bitDepth, raw);
    }

    private static void WritePlte(Stream stream, PngIndexedPaletteBuilder.Plan plan)
    {
        if (plan.TransparentIndex is null)
        {
            PngChunkWriter.WriteChunk(stream, PngChunkType.Plte, plan.Palette);
            return;
        }

        // The transparent index still needs an RGB entry in PLTE (every index referenced by the pixel data
        // must have one), even though its color is never actually visible — tRNS below marks it fully
        // transparent. The extra slot is left at its default (0, 0, 0).
        byte[] withTransparentSlot = new byte[plan.Palette.Length + 3];
        plan.Palette.CopyTo(withTransparentSlot, 0);
        PngChunkWriter.WriteChunk(stream, PngChunkType.Plte, withTransparentSlot);
    }

    private static void WritePaletteTrns(Stream stream, int transparentIndex)
    {
        // Trailing entries default to fully opaque (255) and can be omitted entirely; since the transparent
        // slot is always the last (highest) index in use, this array only needs to run up to and include it.
        byte[] alpha = new byte[transparentIndex + 1];
        for (int i = 0; i < transparentIndex; i++)
        {
            alpha[i] = 255;
        }

        PngChunkWriter.WriteChunk(stream, PngChunkType.Trns, alpha);
    }

    private static void BuildRawRow(Image source, int srcY, in Adam7Pass pass, int passWidth, int bytesPerPixel, int channels, bool is16, Span<byte> raw)
    {
        var srcRow = source.GetRowSpan(srcY);

        if (pass.StrideX == 1)
        {
            if (is16)
            {
                PngBitPacker.PackRow16(srcRow, raw);
            }
            else
            {
                PngBitPacker.PackRow8(srcRow, raw);
            }

            return;
        }

        if (is16)
        {
            var srcSamples = MemoryMarshal.Cast<byte, ushort>(srcRow);
            for (int x = 0; x < passWidth; x++)
            {
                int srcX = pass.StartX + (x * pass.StrideX);
                for (int c = 0; c < channels; c++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(raw.Slice(((x * channels) + c) * 2, 2), srcSamples[(srcX * channels) + c]);
                }
            }

            return;
        }

        for (int x = 0; x < passWidth; x++)
        {
            int srcX = pass.StartX + (x * pass.StrideX);
            srcRow.Slice(srcX * bytesPerPixel, bytesPerPixel).CopyTo(raw.Slice(x * bytesPerPixel, bytesPerPixel));
        }
    }

    private static void WriteTrns(Stream stream, PngColorType colorType, (byte R, byte G, byte B) key, bool is16)
    {
        if (colorType == PngColorType.Grayscale)
        {
            Span<byte> data = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(data, Widen(key.R, is16));
            PngChunkWriter.WriteChunk(stream, PngChunkType.Trns, data);
        }
        else if (colorType == PngColorType.Truecolor)
        {
            Span<byte> data = stackalloc byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(data[..2], Widen(key.R, is16));
            BinaryPrimitives.WriteUInt16BigEndian(data[2..4], Widen(key.G, is16));
            BinaryPrimitives.WriteUInt16BigEndian(data[4..6], Widen(key.B, is16));
            PngChunkWriter.WriteChunk(stream, PngChunkType.Trns, data);
        }
    }

    private static ushort Widen(byte v, bool is16) => is16 ? (ushort)((v << 8) | v) : v;

    private static CompressionLevel MapCompressionLevel(PngCompressionLevel level) => level switch
    {
        PngCompressionLevel.Fastest => CompressionLevel.Fastest,
        PngCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };
}
