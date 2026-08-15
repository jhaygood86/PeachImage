using System.Buffers.Binary;

namespace PeachImage.Formats.Bmp.Decoding;

/// <summary>
/// Converts one already-length-known BMP scanline (padding already stripped, or an index buffer produced by
/// RLE decoding) into a row of decoded pixels. Hot paths are tight scalar loops over <see cref="Span{T}"/> —
/// not routed through a generic per-pixel delegate — since these run on every row of every decode.
/// </summary>
internal static class BmpRowUnpacker
{
    /// <summary>The row size, in bytes, once padded to a 4-byte boundary — BMP's on-disk row layout.</summary>
    public static int GetPaddedRowByteCount(int width, int bitCount) =>
        (((width * bitCount) + 31) / 32) * 4;

    /// <summary>Unpacks a 1/4/8bpp packed row into one palette index per pixel.</summary>
    public static void UnpackBitsToIndices(ReadOnlySpan<byte> rowBytes, int bitCount, Span<byte> indices)
    {
        int width = indices.Length;
        switch (bitCount)
        {
            case 1:
                for (int x = 0; x < width; x++)
                {
                    int byteIndex = x >> 3;
                    int bitOffset = 7 - (x & 7);
                    indices[x] = (byte)((rowBytes[byteIndex] >> bitOffset) & 0x1);
                }

                break;

            case 4:
                for (int x = 0; x < width; x++)
                {
                    byte b = rowBytes[x >> 1];
                    indices[x] = (x & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
                }

                break;

            case 8:
                rowBytes[..width].CopyTo(indices);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Indexed unpacking only supports 1/4/8bpp.");
        }
    }

    /// <summary>Resolves one row of palette indices to RGB24 output via the given palette. Throws on an out-of-range index.</summary>
    public static void ResolveIndexRow(ReadOnlySpan<byte> indices, ReadOnlySpan<byte> palette, Span<byte> destRow)
    {
        int paletteCount = palette.Length / 3;

        for (int x = 0; x < indices.Length; x++)
        {
            int index = indices[x];
            if (index >= paletteCount)
            {
                throw new BmpDecodingException($"BMP pixel references out-of-range palette index {index} (palette has {paletteCount} entries).");
            }

            int srcIndex = index * 3;
            int dstIndex = x * 3;
            destRow[dstIndex + 0] = palette[srcIndex + 0];
            destRow[dstIndex + 1] = palette[srcIndex + 1];
            destRow[dstIndex + 2] = palette[srcIndex + 2];
        }
    }

    /// <summary>Unpacks a 16/24/32bpp direct-color row into RGB24 (<paramref name="hasAlpha"/> false) or RGBA32 (true) output.</summary>
    public static void UnpackDirectColorRow(ReadOnlySpan<byte> rowBytes, int bitCount, BmpHeader header, bool hasAlpha, Span<byte> destRow)
    {
        switch (bitCount)
        {
            case 24:
                Unpack24(rowBytes, destRow);
                break;
            case 16:
                Unpack16(rowBytes, header, hasAlpha, destRow);
                break;
            case 32:
                Unpack32(rowBytes, header, hasAlpha, destRow);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Direct-color unpacking only supports 16/24/32bpp.");
        }
    }

    private static void Unpack24(ReadOnlySpan<byte> rowBytes, Span<byte> destRow)
    {
        int width = destRow.Length / 3;
        for (int x = 0; x < width; x++)
        {
            int srcIndex = x * 3;
            int dstIndex = x * 3;
            destRow[dstIndex + 0] = rowBytes[srcIndex + 2]; // R
            destRow[dstIndex + 1] = rowBytes[srcIndex + 1]; // G
            destRow[dstIndex + 2] = rowBytes[srcIndex + 0]; // B
        }
    }

    private static void Unpack16(ReadOnlySpan<byte> rowBytes, BmpHeader header, bool hasAlpha, Span<byte> destRow)
    {
        var (rMask, gMask, bMask, aMask) = BmpBitfieldMask.ResolveEffectiveMasks(header);
        var rInfo = BmpBitfieldMask.Analyze(rMask);
        var gInfo = BmpBitfieldMask.Analyze(gMask);
        var bInfo = BmpBitfieldMask.Analyze(bMask);
        var aInfo = hasAlpha ? BmpBitfieldMask.Analyze(aMask) : default;

        int bytesPerPixel = hasAlpha ? 4 : 3;
        int width = destRow.Length / bytesPerPixel;

        for (int x = 0; x < width; x++)
        {
            uint packed = BinaryPrimitives.ReadUInt16LittleEndian(rowBytes.Slice(x * 2, 2));
            int dstIndex = x * bytesPerPixel;
            destRow[dstIndex + 0] = BmpBitfieldMask.Extract(packed, rMask, rInfo);
            destRow[dstIndex + 1] = BmpBitfieldMask.Extract(packed, gMask, gInfo);
            destRow[dstIndex + 2] = BmpBitfieldMask.Extract(packed, bMask, bInfo);
            if (hasAlpha)
            {
                destRow[dstIndex + 3] = BmpBitfieldMask.Extract(packed, aMask, aInfo);
            }
        }
    }

    private static void Unpack32(ReadOnlySpan<byte> rowBytes, BmpHeader header, bool hasAlpha, Span<byte> destRow)
    {
        var (rMask, gMask, bMask, aMask) = BmpBitfieldMask.ResolveEffectiveMasks(header);
        int bytesPerPixel = hasAlpha ? 4 : 3;
        int width = destRow.Length / bytesPerPixel;

        if (rMask == 0x00FF0000 && gMask == 0x0000FF00 && bMask == 0x000000FF && (!hasAlpha || aMask == 0xFF000000))
        {
            // Fast path: standard byte-aligned masks — direct byte copy/permute, no shift/scale math needed.
            for (int x = 0; x < width; x++)
            {
                int srcIndex = x * 4;
                int dstIndex = x * bytesPerPixel;
                destRow[dstIndex + 0] = rowBytes[srcIndex + 2]; // R
                destRow[dstIndex + 1] = rowBytes[srcIndex + 1]; // G
                destRow[dstIndex + 2] = rowBytes[srcIndex + 0]; // B
                if (hasAlpha)
                {
                    destRow[dstIndex + 3] = rowBytes[srcIndex + 3]; // A
                }
            }

            return;
        }

        var rInfo = BmpBitfieldMask.Analyze(rMask);
        var gInfo = BmpBitfieldMask.Analyze(gMask);
        var bInfo = BmpBitfieldMask.Analyze(bMask);
        var aInfo = hasAlpha ? BmpBitfieldMask.Analyze(aMask) : default;

        for (int x = 0; x < width; x++)
        {
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(rowBytes.Slice(x * 4, 4));
            int dstIndex = x * bytesPerPixel;
            destRow[dstIndex + 0] = BmpBitfieldMask.Extract(packed, rMask, rInfo);
            destRow[dstIndex + 1] = BmpBitfieldMask.Extract(packed, gMask, gInfo);
            destRow[dstIndex + 2] = BmpBitfieldMask.Extract(packed, bMask, bInfo);
            if (hasAlpha)
            {
                destRow[dstIndex + 3] = BmpBitfieldMask.Extract(packed, aMask, aInfo);
            }
        }
    }
}
