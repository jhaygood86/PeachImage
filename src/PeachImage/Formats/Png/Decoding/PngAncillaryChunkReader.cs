using System.Buffers.Binary;
using System.IO.Compression;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>Parses recognized ancillary chunks (everything except IHDR/PLTE/IDAT/IEND/tRNS) into <see cref="ImageMetadata"/>.</summary>
internal static class PngAncillaryChunkReader
{
    public static void Apply(PngChunkType type, byte[] data, PngHeader header, ImageMetadata metadata)
    {
        if (type == PngChunkType.Gama)
        {
            if (data.Length == 4)
            {
                metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Gamma, Data = data });
            }
        }
        else if (type == PngChunkType.Chrm)
        {
            if (data.Length == 32)
            {
                metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Chromaticity, Data = data });
            }
        }
        else if (type == PngChunkType.Srgb)
        {
            if (data.Length == 1)
            {
                metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Srgb, Data = data });
            }
        }
        else if (type == PngChunkType.Time)
        {
            if (data.Length == 7)
            {
                metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Time, Data = data });
            }
        }
        else if (type == PngChunkType.Bkgd)
        {
            metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Background, Data = data });
        }
        else if (type == PngChunkType.Iccp)
        {
            TryReadIccp(data, metadata);
        }
        else if (type == PngChunkType.Phys)
        {
            TryReadPhys(data, metadata);
        }
        else if (type == PngChunkType.Text)
        {
            TryReadText(data, metadata);
        }
        else if (type == PngChunkType.Ztxt)
        {
            TryReadZtxt(data, metadata);
        }
        else if (type == PngChunkType.Itxt)
        {
            TryReadItxt(data, metadata);
        }
    }

    private static void TryReadIccp(byte[] data, ImageMetadata metadata)
    {
        int nullIndex = Array.IndexOf(data, (byte)0);
        if (nullIndex < 0 || nullIndex + 1 >= data.Length)
        {
            return;
        }

        byte compressionMethod = data[nullIndex + 1];
        if (compressionMethod != 0)
        {
            return;
        }

        var compressed = data.AsSpan(nullIndex + 2).ToArray();
        byte[]? profileBytes = TryInflate(compressed);
        if (profileBytes is not null)
        {
            metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = profileBytes });
        }
    }

    private static void TryReadPhys(byte[] data, ImageMetadata metadata)
    {
        if (data.Length != 9)
        {
            return;
        }

        uint pixelsPerUnitX = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        uint pixelsPerUnitY = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        byte unitSpecifier = data[8];

        if (unitSpecifier == 1)
        {
            metadata.HorizontalResolution = pixelsPerUnitX;
            metadata.VerticalResolution = pixelsPerUnitY;
        }
    }

    private static void TryReadText(byte[] data, ImageMetadata metadata)
    {
        int nullIndex = Array.IndexOf(data, (byte)0);
        if (nullIndex < 0)
        {
            return;
        }

        string keyword = Latin1.GetString(data, 0, nullIndex);
        string text = Latin1.GetString(data, nullIndex + 1, data.Length - nullIndex - 1);
        AddTextEntry(metadata, keyword, string.Empty, string.Empty, text);
    }

    private static void TryReadZtxt(byte[] data, ImageMetadata metadata)
    {
        int nullIndex = Array.IndexOf(data, (byte)0);
        if (nullIndex < 0 || nullIndex + 1 >= data.Length)
        {
            return;
        }

        byte compressionMethod = data[nullIndex + 1];
        if (compressionMethod != 0)
        {
            return;
        }

        string keyword = Latin1.GetString(data, 0, nullIndex);
        byte[]? textBytes = TryInflate(data.AsSpan(nullIndex + 2).ToArray());
        if (textBytes is not null)
        {
            AddTextEntry(metadata, keyword, string.Empty, string.Empty, Latin1.GetString(textBytes));
        }
    }

    private static void TryReadItxt(byte[] data, ImageMetadata metadata)
    {
        int firstNull = Array.IndexOf(data, (byte)0);
        if (firstNull < 0 || firstNull + 2 >= data.Length)
        {
            return;
        }

        string keyword = Latin1.GetString(data, 0, firstNull);
        byte compressionFlag = data[firstNull + 1];
        byte compressionMethod = data[firstNull + 2];

        int languageStart = firstNull + 3;
        int secondNull = Array.IndexOf(data, (byte)0, languageStart);
        if (secondNull < 0)
        {
            return;
        }

        string languageTag = Latin1.GetString(data, languageStart, secondNull - languageStart);

        int translatedStart = secondNull + 1;
        int thirdNull = Array.IndexOf(data, (byte)0, translatedStart);
        if (thirdNull < 0)
        {
            return;
        }

        string translatedKeyword = System.Text.Encoding.UTF8.GetString(data, translatedStart, thirdNull - translatedStart);

        int textStart = thirdNull + 1;
        byte[] textBytes = data.AsSpan(textStart).ToArray();

        string text;
        if (compressionFlag == 1)
        {
            if (compressionMethod != 0)
            {
                return;
            }

            byte[]? inflated = TryInflate(textBytes);
            if (inflated is null)
            {
                return;
            }

            text = System.Text.Encoding.UTF8.GetString(inflated);
        }
        else
        {
            text = System.Text.Encoding.UTF8.GetString(textBytes);
        }

        AddTextEntry(metadata, keyword, languageTag, translatedKeyword, text);
    }

    private static void AddTextEntry(ImageMetadata metadata, string keyword, string languageTag, string translatedKeyword, string text)
    {
        var entry = new PngTextEntry
        {
            Keyword = keyword,
            LanguageTag = languageTag,
            TranslatedKeyword = translatedKeyword,
            Text = text,
        };
        metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Text, Data = BuildTextData(entry) });
    }

    private static byte[] BuildTextData(PngTextEntry entry) =>
        System.Text.Encoding.UTF8.GetBytes(string.Concat(entry.Keyword, "\0", entry.LanguageTag, "\0", entry.TranslatedKeyword, "\0", entry.Text));

    private static byte[]? TryInflate(byte[] zlibCompressed)
    {
        try
        {
            using var source = new MemoryStream(zlibCompressed);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            using var result = new MemoryStream();
            zlib.CopyTo(result);
            return result.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static readonly System.Text.Encoding Latin1 = System.Text.Encoding.Latin1;
}
