using System.Buffers.Binary;
using System.IO.Compression;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>
/// Re-emits captured metadata as ancillary chunks, gated by <see cref="EncoderOptions.IncludeMetadata"/>.
/// Split into two passes so callers can interleave a PLTE write between them: spec §5.4 requires cHRM/gAMA/
/// sRGB/iCCP to precede PLTE, while bKGD must follow it (and pHYs/tEXt/tIME aren't order-constrained either way).
/// </summary>
internal static class PngAncillaryChunkWriter
{
    /// <summary>Chunks that must precede PLTE (and IDAT): color-management data the palette itself depends on.</summary>
    public static void WriteColorProfileChunks(Stream stream, ImageMetadata metadata)
    {
        foreach (var profile in metadata.Profiles)
        {
            switch (profile.Kind)
            {
                case MetadataProfileKind.Icc:
                    WriteIccp(stream, profile.Data);
                    break;

                case MetadataProfileKind.Gamma when profile.Data.Length == 4:
                    PngChunkWriter.WriteChunk(stream, PngChunkType.Gama, profile.Data);
                    break;

                case MetadataProfileKind.Chromaticity when profile.Data.Length == 32:
                    PngChunkWriter.WriteChunk(stream, PngChunkType.Chrm, profile.Data);
                    break;

                case MetadataProfileKind.Srgb when profile.Data.Length == 1:
                    PngChunkWriter.WriteChunk(stream, PngChunkType.Srgb, profile.Data);
                    break;
            }
        }
    }

    /// <summary>Everything else: no ordering constraint beyond preceding IDAT, but bKGD additionally must not precede PLTE — the caller guarantees that by calling this only after any PLTE write.</summary>
    public static void WriteRemainingChunks(Stream stream, ImageMetadata metadata)
    {
        if (metadata.HorizontalResolution is { } hRes && metadata.VerticalResolution is { } vRes && hRes > 0 && vRes > 0)
        {
            WritePhys(stream, hRes, vRes);
        }

        foreach (var profile in metadata.Profiles)
        {
            switch (profile.Kind)
            {
                case MetadataProfileKind.Time when profile.Data.Length == 7:
                    PngChunkWriter.WriteChunk(stream, PngChunkType.Time, profile.Data);
                    break;

                case MetadataProfileKind.Background:
                    PngChunkWriter.WriteChunk(stream, PngChunkType.Bkgd, profile.Data);
                    break;

                case MetadataProfileKind.Text:
                    if (PngTextEntry.TryParse(profile, out var entry) && entry is not null)
                    {
                        WriteItxt(stream, entry);
                    }

                    break;
            }
        }
    }

    private static void WritePhys(Stream stream, double horizontalPixelsPerMeter, double verticalPixelsPerMeter)
    {
        Span<byte> data = stackalloc byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(data[..4], (uint)Math.Round(horizontalPixelsPerMeter));
        BinaryPrimitives.WriteUInt32BigEndian(data[4..8], (uint)Math.Round(verticalPixelsPerMeter));
        data[8] = 1; // unit specifier: meter
        PngChunkWriter.WriteChunk(stream, PngChunkType.Phys, data);
    }

    private static void WriteIccp(Stream stream, byte[] iccProfileBytes)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(iccProfileBytes);
        }

        byte[] name = "ICC Profile"u8.ToArray();
        var data = new byte[name.Length + 2 + compressed.Length];
        name.CopyTo(data, 0);
        data[name.Length] = 0; // null terminator
        data[name.Length + 1] = 0; // compression method
        compressed.ToArray().CopyTo(data, name.Length + 2);

        PngChunkWriter.WriteChunk(stream, PngChunkType.Iccp, data);
    }

    private static void WriteItxt(Stream stream, PngTextEntry entry)
    {
        byte[] keyword = System.Text.Encoding.Latin1.GetBytes(entry.Keyword);
        byte[] language = System.Text.Encoding.Latin1.GetBytes(entry.LanguageTag);
        byte[] translated = System.Text.Encoding.UTF8.GetBytes(entry.TranslatedKeyword);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(entry.Text);

        var data = new byte[keyword.Length + 1 + 2 + language.Length + 1 + translated.Length + 1 + text.Length];
        int offset = 0;
        keyword.CopyTo(data, offset);
        offset += keyword.Length;
        data[offset++] = 0;
        data[offset++] = 0; // compression flag: uncompressed
        data[offset++] = 0; // compression method
        language.CopyTo(data, offset);
        offset += language.Length;
        data[offset++] = 0;
        translated.CopyTo(data, offset);
        offset += translated.Length;
        data[offset++] = 0;
        text.CopyTo(data, offset);

        PngChunkWriter.WriteChunk(stream, PngChunkType.Itxt, data);
    }
}
