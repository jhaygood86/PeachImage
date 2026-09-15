using PeachImage.Formats.Png.Decoding;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png;

/// <summary>
/// Reads a PNG's IHDR/PLTE/tRNS/IDAT chunk data verbatim, with no pixel decode (no inflate), for callers
/// that want to re-embed the original compressed pixel stream in another container — e.g. a PDF
/// <c>/FlateDecode</c> image stream, which can consume a PNG's zlib-compressed IDAT data directly — rather
/// than decode it into an <see cref="Image"/> and re-encode it.
/// </summary>
public static class PngPassthrough
{
    /// <summary>
    /// Attempts to read <paramref name="info"/> from <paramref name="stream"/>. Returns <see langword="false"/>
    /// (never throws) if the stream is not a PNG, or is structurally invalid or CRC-corrupt — matching
    /// <see cref="Image.TryLoad"/>'s contract. Succeeds for interlaced, transparent, and animated PNGs
    /// alike; callers should check <see cref="PngPassthroughInfo.IsInterlaced"/>,
    /// <see cref="PngPassthroughInfo.HasTrns"/>, and <see cref="PngPassthroughInfo.IsAnimated"/> themselves
    /// to decide whether the raw data is usable for their target format.
    /// </summary>
    public static bool TryRead(Stream stream, out PngPassthroughInfo info)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            info = Read(stream);
            return true;
        }
        catch (PngDecodingException)
        {
            info = default;
            return false;
        }
    }

    private static PngPassthroughInfo Read(Stream stream)
    {
        PngChunkReader.ReadSignature(stream);
        var header = PngHeaderReader.ReadIhdr(stream);

        byte[]? paletteData = null;
        byte[]? trnsData = null;
        bool isAnimated = false;

        var chunkHeader = PngChunkReader.ReadHeader(stream);
        while (chunkHeader.Type != PngChunkType.Idat)
        {
            if (chunkHeader.Type == PngChunkType.Iend)
            {
                throw new PngDecodingException("PNG file has no IDAT chunk.");
            }

            if (chunkHeader.Type == PngChunkType.Plte)
            {
                paletteData = PngChunkReader.ReadDataAndValidateCrc(stream, chunkHeader);
            }
            else if (chunkHeader.Type == PngChunkType.Trns)
            {
                trnsData = PngChunkReader.ReadDataAndValidateCrc(stream, chunkHeader);
            }
            else
            {
                if (chunkHeader.Type == PngChunkType.Actl)
                {
                    isAnimated = true;
                }

                PngChunkReader.SkipChunk(stream, chunkHeader);
            }

            chunkHeader = PngChunkReader.ReadHeader(stream);
        }

        if (header.ColorType == PngColorType.Palette && paletteData is null)
        {
            throw new PngDecodingException("Palette (color type 3) image is missing a required PLTE chunk.");
        }

        var idatStream = new PngIdatStream(stream);
        idatStream.BeginChunk(chunkHeader);
        using var idatBuffer = new MemoryStream();
        idatStream.CopyTo(idatBuffer);

        return new PngPassthroughInfo(
            header.Width,
            header.Height,
            header.ColorType,
            header.BitDepth,
            header.IsInterlaced,
            HasTrns: trnsData is not null,
            IsAnimated: isAnimated,
            IdatData: idatBuffer.ToArray(),
            PaletteData: paletteData,
            TrnsData: trnsData);
    }
}
