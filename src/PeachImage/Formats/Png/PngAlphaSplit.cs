using System.IO.Compression;
using PeachImage.Formats.Png.Decoding;
using PeachImage.Formats.Png.Filtering;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png;

/// <summary>
/// Splits a PNG's interleaved color+alpha <c>IDAT</c> data into two independent, ready-to-embed zlib
/// (RFC 1950) streams — a color-only plane and a DeviceGray alpha-only plane — for callers that want
/// to build a PDF color XObject + <c>/SMask</c> pair without doing a full <see cref="PixelFormat.Rgba32"/>
/// pixel decode. See <see cref="TrySplit(in PngPassthroughInfo, out PngAlphaSplitInfo)"/>.
/// </summary>
public static class PngAlphaSplit
{
    /// <summary>
    /// Attempts to split the alpha channel out of an already-read <paramref name="passthrough"/> (e.g.
    /// from <see cref="PngPassthrough.TryRead"/>). Returns <see langword="false"/> (never throws) when
    /// the source isn't eligible.
    /// </summary>
    /// <remarks>
    /// Eligible sources are color type 4 (GrayscaleAlpha), color type 6 (TruecolorAlpha) — both only at
    /// 8-bit depth in this version — or a palette (color type 3) image whose <c>tRNS</c> chunk contains
    /// at least one genuine partial-alpha entry (a byte that is neither 0 nor 255). A palette image
    /// whose transparency is purely binary (every <c>tRNS</c> byte is 0 or 255) is better expressed as a
    /// PDF color-key <c>/Mask</c> built directly from <see cref="PngPassthroughInfo.TrnsData"/>, and is
    /// not considered eligible here. Interlaced sources are never eligible: Adam7 sub-image data cannot
    /// be de-interleaved by this simple a row loop.
    /// <para/>
    /// For the palette case, <see cref="PngAlphaSplitInfo.ColorData"/> is always <see langword="null"/>
    /// — the color side needs no new data at all; pair the existing
    /// <see cref="PngPassthroughInfo.IdatData"/> and <see cref="PngPassthroughInfo.PaletteData"/> with a
    /// PDF <c>/Indexed</c> color space as usual, and use only the returned alpha plane as the
    /// <c>/SMask</c>.
    /// </remarks>
    public static bool TrySplit(in PngPassthroughInfo passthrough, out PngAlphaSplitInfo info)
    {
        try
        {
            return TrySplitCore(in passthrough, out info);
        }
        catch (PngDecodingException)
        {
            info = default;
            return false;
        }
    }

    /// <summary>Reads a PNG from <paramref name="stream"/> (via <see cref="PngPassthrough.TryRead"/>) and splits it. Never throws.</summary>
    public static bool TryRead(Stream stream, out PngAlphaSplitInfo info)
    {
        if (!PngPassthrough.TryRead(stream, out var passthrough))
        {
            info = default;
            return false;
        }

        return TrySplit(in passthrough, out info);
    }

    private static bool TrySplitCore(in PngPassthroughInfo passthrough, out PngAlphaSplitInfo info)
    {
        if (passthrough.IsInterlaced)
        {
            info = default;
            return false;
        }

        switch (passthrough.ColorType)
        {
            case PngColorType.GrayscaleAlpha:
                return TrySplitInterleaved(in passthrough, samplesPerPixel: 2, colorIsRgb: false, out info);

            case PngColorType.TruecolorAlpha:
                return TrySplitInterleaved(in passthrough, samplesPerPixel: 4, colorIsRgb: true, out info);

            case PngColorType.Palette:
                return TrySplitPaletteAlpha(in passthrough, out info);

            default:
                info = default;
                return false;
        }
    }

    private static bool TrySplitInterleaved(in PngPassthroughInfo passthrough, int samplesPerPixel, bool colorIsRgb, out PngAlphaSplitInfo info)
    {
        if (passthrough.BitDepth != 8)
        {
            // v1 limitation: 16-bit color-type-4/6 sources aren't split yet.
            info = default;
            return false;
        }

        var header = new PngHeader(passthrough.Width, passthrough.Height, passthrough.BitDepth, passthrough.ColorType, 0, 0, 0);
        int colorSamplesPerPixel = samplesPerPixel - 1;
        int bytesPerRow = PngHeader.BytesPerScanline(passthrough.Width, header.BitsPerPixel);

        using var colorPlane = new MemoryStream();
        using var alphaPlane = new MemoryStream();

        using (var colorZlib = new ZLibStream(colorPlane, CompressionLevel.Optimal, leaveOpen: true))
        using (var alphaZlib = new ZLibStream(alphaPlane, CompressionLevel.Optimal, leaveOpen: true))
        {
            using var idat = new MemoryStream(passthrough.IdatData);
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);

            byte[] previousRow = new byte[bytesPerRow];
            byte[] currentRow = new byte[bytesPerRow];
            byte[] colorRow = new byte[passthrough.Width * colorSamplesPerPixel];
            byte[] alphaRow = new byte[passthrough.Width];

            for (int y = 0; y < passthrough.Height; y++)
            {
                byte filterType = ReadByteOrThrow(inflate);
                ReadExactlyOrThrow(inflate, currentRow);
                RowFilter.Unfilter(currentRow, previousRow, (PngFilterType)filterType, header.FilterBytesPerPixel);

                for (int x = 0; x < passthrough.Width; x++)
                {
                    int srcOffset = x * samplesPerPixel;
                    int colorOffset = x * colorSamplesPerPixel;
                    for (int c = 0; c < colorSamplesPerPixel; c++)
                    {
                        colorRow[colorOffset + c] = currentRow[srcOffset + c];
                    }

                    alphaRow[x] = currentRow[srcOffset + colorSamplesPerPixel];
                }

                colorZlib.WriteByte(0);
                colorZlib.Write(colorRow);
                alphaZlib.WriteByte(0);
                alphaZlib.Write(alphaRow);

                (previousRow, currentRow) = (currentRow, previousRow);
            }
        }

        info = new PngAlphaSplitInfo(
            passthrough.Width,
            passthrough.Height,
            AlphaBitDepth: 8,
            AlphaData: alphaPlane.ToArray(),
            ColorData: colorPlane.ToArray(),
            ColorBitDepth: 8,
            ColorIsRgb: colorIsRgb);
        return true;
    }

    private static bool TrySplitPaletteAlpha(in PngPassthroughInfo passthrough, out PngAlphaSplitInfo info)
    {
        if (!passthrough.HasTrns ||
            passthrough.TrnsData is not { Length: > 0 } trnsData ||
            passthrough.PaletteData is not { Length: > 0 } paletteData)
        {
            info = default;
            return false;
        }

        bool hasPartialAlpha = false;
        foreach (byte a in trnsData)
        {
            if (a is > 0 and < 255)
            {
                hasPartialAlpha = true;
                break;
            }
        }

        if (!hasPartialAlpha)
        {
            info = default;
            return false;
        }

        var header = new PngHeader(passthrough.Width, passthrough.Height, passthrough.BitDepth, passthrough.ColorType, 0, 0, 0);
        int bytesPerRow = PngHeader.BytesPerScanline(passthrough.Width, header.BitsPerPixel);
        var palette = PngPalette.Read(paletteData);
        palette.SetAlpha(trnsData);

        using var alphaPlane = new MemoryStream();

        using (var alphaZlib = new ZLibStream(alphaPlane, CompressionLevel.Optimal, leaveOpen: true))
        {
            using var idat = new MemoryStream(passthrough.IdatData);
            using var inflate = new ZLibStream(idat, CompressionMode.Decompress);

            byte[] previousRow = new byte[bytesPerRow];
            byte[] currentRow = new byte[bytesPerRow];
            ushort[] indices = new ushort[passthrough.Width];
            byte[] alphaRow = new byte[passthrough.Width];

            for (int y = 0; y < passthrough.Height; y++)
            {
                byte filterType = ReadByteOrThrow(inflate);
                ReadExactlyOrThrow(inflate, currentRow);
                RowFilter.Unfilter(currentRow, previousRow, (PngFilterType)filterType, header.FilterBytesPerPixel);

                PngBitUnpacker.Unpack(currentRow, passthrough.BitDepth, passthrough.Width, samplesPerPixel: 1, indices);
                for (int x = 0; x < passthrough.Width; x++)
                {
                    alphaRow[x] = palette.Resolve(indices[x]).A;
                }

                alphaZlib.WriteByte(0);
                alphaZlib.Write(alphaRow);

                (previousRow, currentRow) = (currentRow, previousRow);
            }
        }

        info = new PngAlphaSplitInfo(
            passthrough.Width,
            passthrough.Height,
            AlphaBitDepth: 8,
            AlphaData: alphaPlane.ToArray(),
            ColorData: null,
            ColorBitDepth: 0,
            ColorIsRgb: false);
        return true;
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
            throw new PngDecodingException("Unexpected end of stream or corrupt compressed data while splitting a PNG's alpha channel.", ex);
        }
    }
}
