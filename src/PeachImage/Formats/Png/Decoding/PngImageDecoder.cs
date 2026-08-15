using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using PeachImage.Formats.Png.Filtering;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>Top-level PNG decode orchestrator: metadata chunks, a streaming (no-buffering) pixel decode, then trailing chunks.</summary>
internal static class PngImageDecoder
{
    public static Image Decode(Stream stream, PngDecoderOptions? options)
    {
        PngChunkReader.ReadSignature(stream);
        var header = PngHeaderReader.ReadIhdr(stream);

        PngPalette? palette = null;
        ushort[]? grayOrRgbTrnsKey = null;
        uint? gammaHundredThousandths = null;
        bool sawSrgb = false;
        var metadata = new ImageMetadata();

        PngChunkHeader chunkHeader = PngChunkReader.ReadHeader(stream);
        while (chunkHeader.Type != PngChunkType.Idat)
        {
            if (chunkHeader.Type == PngChunkType.Iend)
            {
                throw new PngDecodingException("PNG file has no IDAT chunk.");
            }

            ProcessMetadataChunk(stream, chunkHeader, header, metadata, ref palette, ref grayOrRgbTrnsKey, ref gammaHundredThousandths, ref sawSrgb);
            chunkHeader = PngChunkReader.ReadHeader(stream);
        }

        if (header.ColorType == PngColorType.Palette && palette is null)
        {
            throw new PngDecodingException("Palette (color type 3) image is missing a required PLTE chunk.");
        }

        bool hasTrns = header.ColorType switch
        {
            PngColorType.Grayscale or PngColorType.Truecolor => grayOrRgbTrnsKey is not null,
            PngColorType.Palette => palette!.HasTransparency,
            _ => false,
        };

        var outputFormat = PngPixelFormatSelector.Choose(header, hasTrns);
        bool is16BitOutput = outputFormat.GetBytesPerSample() == 2;

        double? fileGamma = gammaHundredThousandths is { } g ? g / 100_000.0 : sawSrgb ? 1.0 / 2.2 : null;
        bool gammaCorrectionActive = fileGamma is not null && options?.ScreenGamma is > 0;
        var sampleLut = PngSampleLut.Build(header.BitDepth, fileGamma, options?.ScreenGamma);

        Image image;
        try
        {
            image = Image.Create(header.Width, header.Height, outputFormat);
        }
        catch (OverflowException ex)
        {
            // Reachable even though width*height is already bounded by MaxPixelCount: that check doesn't
            // account for pixel format byte size, and an 8-bytes/pixel format (Rgba64, from a 16-bit image
            // with a tRNS chunk) can overflow Image.Create's byte-count math at the pixel-count ceiling.
            throw new PngDecodingException($"PNG dimensions {header.Width}x{header.Height} at pixel format {outputFormat} require too large an allocation to decode.", ex);
        }

        foreach (var profile in metadata.Profiles)
        {
            image.Metadata.Profiles.Add(profile);
        }

        image.Metadata.HorizontalResolution = metadata.HorizontalResolution;
        image.Metadata.VerticalResolution = metadata.VerticalResolution;

        var idatStream = new PngIdatStream(stream);
        idatStream.BeginChunk(chunkHeader);

        DecodePixels(idatStream, header, palette, grayOrRgbTrnsKey, sampleLut, is16BitOutput, gammaCorrectionActive, outputFormat, image);

        idatStream.DrainToNextChunk();
        var nextHeader = idatStream.PendingNonIdatHeader!.Value;

        // Trailing chunks (post-IDAT ancillary chunks are spec-legal, e.g. tEXt/tIME) up to IEND.
        // nextHeader's bytes were already consumed from the stream while PngIdatStream was peeking for
        // the end of the IDAT run, so the first iteration processes it directly rather than re-reading.
        while (nextHeader.Type != PngChunkType.Iend)
        {
            if (nextHeader.Type == PngChunkType.Idat)
            {
                throw new PngDecodingException("PNG file has non-consecutive IDAT chunks.");
            }

            ProcessMetadataChunk(stream, nextHeader, header, metadata, ref palette, ref grayOrRgbTrnsKey, ref gammaHundredThousandths, ref sawSrgb);
            nextHeader = PngChunkReader.ReadHeader(stream);
        }

        PngChunkReader.ReadDataAndValidateCrc(stream, nextHeader);

        return image;
    }

    private static void DecodePixels(Stream idatStream, PngHeader header, PngPalette? palette, ushort[]? grayOrRgbTrnsKey, ushort[] sampleLut, bool is16BitOutput, bool gammaCorrectionActive, PixelFormat outputFormat, Image image)
    {
        using var zlib = new ZLibStream(idatStream, CompressionMode.Decompress);

        ReadOnlySpan<Adam7Pass> passes = header.IsInterlaced ? Adam7.Passes : [new Adam7Pass(0, 0, 1, 1)];
        int destBpp = outputFormat.GetBytesPerPixel();

        // Fast paths: for grayscale/truecolor/truecolor+alpha with no tRNS-driven alpha synthesis (tRNS
        // is meaningless for color type 6 anyway) and no active gamma correction, PNG's on-disk sample
        // layout already matches PeachImage's pixel layout up to byte order — no per-sample scaling or
        // LUT lookup is ever needed for these color types. At 8-bit depth the defiltered row IS the final
        // pixel row (a true zero-transform case); at 16-bit depth it still needs a big-endian-to-native
        // byte swap, but skips the ushort-sample-buffer round trip and LUT indirection the general path
        // requires. This covers the overwhelmingly common real-world case (photos/screenshots/web
        // graphics) and skips one or two full O(width) passes per row.
        bool directCopy8 = header.BitDepth == 8 && grayOrRgbTrnsKey is null &&
            header.ColorType is PngColorType.Grayscale or PngColorType.Truecolor or PngColorType.TruecolorAlpha;
        bool directCopy16 = header.BitDepth == 16 && grayOrRgbTrnsKey is null && !gammaCorrectionActive &&
            header.ColorType is PngColorType.Grayscale or PngColorType.Truecolor or PngColorType.TruecolorAlpha;

        foreach (var pass in passes)
        {
            var (passWidth, passHeight) = header.IsInterlaced
                ? Adam7.GetPassDimensions(header.Width, header.Height, pass)
                : (header.Width, header.Height);

            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }

            int bytesPerRow = PngHeader.BytesPerScanline(passWidth, header.BitsPerPixel);
            var previousRowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);
            var currentRowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);
            var samplesBuffer = (directCopy8 || directCopy16) ? null : ArrayPool<ushort>.Shared.Rent(passWidth * header.SamplesPerPixel);
            var resolvedRowBuffer = directCopy8 ? null : ArrayPool<byte>.Shared.Rent(passWidth * destBpp);
            try
            {
                var previousRow = previousRowBuffer.AsSpan(0, bytesPerRow);
                var currentRow = currentRowBuffer.AsSpan(0, bytesPerRow);
                previousRow.Clear();

                for (int y = 0; y < passHeight; y++)
                {
                    byte filterTypeByte = ReadByteOrThrow(zlib);
                    ReadExactlyOrThrow(zlib, currentRow);
                    RowFilter.Unfilter(currentRow, previousRow, (PngFilterType)filterTypeByte, header.FilterBytesPerPixel);

                    ReadOnlySpan<byte> sourceRow;
                    if (directCopy8)
                    {
                        sourceRow = currentRow;
                    }
                    else if (directCopy16)
                    {
                        var resolvedRow = resolvedRowBuffer.AsSpan(0, passWidth * destBpp);
                        SwapBigEndian16(currentRow, resolvedRow);
                        sourceRow = resolvedRow;
                    }
                    else
                    {
                        var samples = samplesBuffer.AsSpan(0, passWidth * header.SamplesPerPixel);
                        var resolvedRow = resolvedRowBuffer.AsSpan(0, passWidth * destBpp);
                        PngBitUnpacker.Unpack(currentRow, header.BitDepth, passWidth, header.SamplesPerPixel, samples);
                        PngRowResolver.Resolve(samples, passWidth, header, palette, grayOrRgbTrnsKey, sampleLut, is16BitOutput, resolvedRow);
                        sourceRow = resolvedRow;
                    }

                    int destY = pass.StartY + (y * pass.StrideY);
                    var destRow = image.GetRowSpan(destY);
                    if (pass.StrideX == 1)
                    {
                        sourceRow.CopyTo(destRow);
                    }
                    else
                    {
                        for (int x = 0; x < passWidth; x++)
                        {
                            int destX = pass.StartX + (x * pass.StrideX);
                            sourceRow.Slice(x * destBpp, destBpp).CopyTo(destRow.Slice(destX * destBpp, destBpp));
                        }
                    }

                    var swapRow = previousRow;
                    previousRow = currentRow;
                    currentRow = swapRow;

                    (previousRowBuffer, currentRowBuffer) = (currentRowBuffer, previousRowBuffer);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(previousRowBuffer);
                ArrayPool<byte>.Shared.Return(currentRowBuffer);
                if (samplesBuffer is not null)
                {
                    ArrayPool<ushort>.Shared.Return(samplesBuffer);
                }

                if (resolvedRowBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(resolvedRowBuffer);
                }
            }
        }
    }

    /// <summary>Reads big-endian 16-bit samples from <paramref name="bigEndianBytes"/> and writes them as native-endian <see cref="ushort"/>s into <paramref name="destination"/> — the fused equivalent of an identity-LUT <see cref="PngBitUnpacker.Unpack"/> + <see cref="PngRowResolver.Resolve"/> pass for the direct-copy-eligible color types.</summary>
    private static void SwapBigEndian16(ReadOnlySpan<byte> bigEndianBytes, Span<byte> destination)
    {
        var dest = MemoryMarshal.Cast<byte, ushort>(destination);
        for (int i = 0; i < dest.Length; i++)
        {
            dest[i] = BinaryPrimitives.ReadUInt16BigEndian(bigEndianBytes.Slice(i * 2, 2));
        }
    }

    /// <summary>Processes an already-read chunk header that is known not to be IDAT/IEND: PLTE, tRNS, or a recognized/unrecognized ancillary chunk.</summary>
    private static void ProcessMetadataChunk(Stream stream, PngChunkHeader chunkHeader, PngHeader header, ImageMetadata metadata, ref PngPalette? palette, ref ushort[]? grayOrRgbTrnsKey, ref uint? gammaHundredThousandths, ref bool sawSrgb)
    {
        if (chunkHeader.Type == PngChunkType.Plte)
        {
            byte[] data = PngChunkReader.ReadDataAndValidateCrc(stream, chunkHeader);
            palette = PngPalette.Read(data);
            return;
        }

        if (chunkHeader.Type == PngChunkType.Trns)
        {
            byte[] data = PngChunkReader.ReadDataAndValidateCrc(stream, chunkHeader);
            ApplyTrns(data, header, palette, ref grayOrRgbTrnsKey);
            return;
        }

        if (chunkHeader.Type.IsCritical)
        {
            throw new PngDecodingException($"Unrecognized critical chunk '{chunkHeader.Type}'.");
        }

        if (chunkHeader.Length > PngDecodingLimits.MaxAncillaryChunkBytes)
        {
            throw new PngDecodingException($"Ancillary chunk '{chunkHeader.Type}' declares {chunkHeader.Length} bytes, exceeding the {PngDecodingLimits.MaxAncillaryChunkBytes}-byte limit.");
        }

        byte[] ancillaryData = PngChunkReader.ReadDataAndValidateCrc(stream, chunkHeader);
        PngAncillaryChunkReader.Apply(chunkHeader.Type, ancillaryData, header, metadata);

        if (chunkHeader.Type == PngChunkType.Gama && ancillaryData.Length == 4)
        {
            gammaHundredThousandths = BinaryPrimitives.ReadUInt32BigEndian(ancillaryData);
        }
        else if (chunkHeader.Type == PngChunkType.Srgb)
        {
            sawSrgb = true;
        }
    }

    private static void ApplyTrns(byte[] data, PngHeader header, PngPalette? palette, ref ushort[]? grayOrRgbTrnsKey)
    {
        switch (header.ColorType)
        {
            case PngColorType.Grayscale:
            case PngColorType.Truecolor:
                grayOrRgbTrnsKey = PngTrnsReader.ReadKey(data, header.ColorType);
                return;

            case PngColorType.Palette:
                if (palette is null)
                {
                    throw new PngDecodingException("tRNS chunk for a palette image must follow a PLTE chunk.");
                }

                if (data.Length > 256)
                {
                    throw new PngDecodingException($"tRNS chunk declares {data.Length} entries, exceeding the 256-entry palette limit.");
                }

                palette.SetAlpha(data);
                return;

            default:
                // tRNS is not meaningful for color types that already carry a full alpha channel (4/6);
                // tolerate its presence without erroring, matching PeachImage's graceful-decode posture.
                return;
        }
    }

    private static byte ReadByteOrThrow(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[1];
        ReadExactlyOrThrow(stream, buffer);
        return buffer[0];
    }

    private static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException)
        {
            throw new PngDecodingException("Unexpected end of stream or corrupt compressed data while decoding PNG pixel data.", ex);
        }
    }
}
