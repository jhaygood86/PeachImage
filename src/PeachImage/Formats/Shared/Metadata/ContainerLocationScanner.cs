using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace PeachImage.Formats.Shared.Metadata;

/// <summary>
/// Finds location metadata in a JPEG, PNG, WebP or TIFF stream by walking the container's own structure, without decoding any
/// pixels. The stream must be seekable and is left at its original position. Everything read is bounded by the bytes the
/// stream really holds and by fixed caps, never by a size a file declares.
/// </summary>
internal static class ContainerLocationScanner
{
    /// <summary>The most bytes of one EXIF block read from a PNG or WebP chunk.</summary>
    public const int MaxExifBytes = 4 << 20;

    /// <summary>The most bytes of a TIFF file read to look for its metadata tags.</summary>
    public const int MaxTiffBytes = 64 << 20;

    private const int MaxChunks = 100_000;
    private const int MaxJpegSegments = 4096;

    private static ReadOnlySpan<byte> JpegXmpHeader => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> PngXmpKeyword => "XML:com.adobe.xmp\0"u8;

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Whether <paramref name="formatName"/> is a format this scanner understands.</summary>
    public static bool Supports(string formatName) => formatName is "jpeg" or "png" or "webp" or "tiff" or "gif" or "avif" or "jxl";

    /// <summary>Scans the whole of <paramref name="stream"/>, from its current position, as <paramref name="formatName"/>; the position is restored.</summary>
    public static LocationInfo Scan(Stream stream, string formatName)
    {
        long start = stream.Position;
        try
        {
            return formatName switch
            {
                "jpeg" => ScanJpeg(stream),
                "png" => ScanPng(stream),
                "webp" => ScanWebp(stream),
                "tiff" => ScanTiff(stream),
                "gif" => ScanGif(stream),
                "avif" => ScanAvif(stream),
                "jxl" => ScanJxl(stream),
                _ => default,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ImageFormatException)
        {
            return default;
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static LocationInfo ScanJpeg(Stream stream)
    {
        var result = default(LocationInfo);
        Span<byte> head = stackalloc byte[4];
        if (stream.Read(head[..2]) != 2 || head[0] != 0xFF || head[1] != 0xD8)
        {
            return result;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            for (int segment = 0; segment < MaxJpegSegments; segment++)
            {
                int first = stream.ReadByte();
                if (first == -1)
                {
                    return result;
                }

                if (first != 0xFF)
                {
                    continue; // stray byte between segments: resynchronise on the next 0xFF.
                }

                int b;
                do
                {
                    b = stream.ReadByte(); // 0xFF bytes may pad before a marker.
                }
                while (b == 0xFF);

                if (b == -1)
                {
                    return result;
                }

                if (b == 0x00)
                {
                    continue;
                }

                if (b is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    continue;
                }

                if (b is 0xD9 or 0xDA)
                {
                    return result;
                }

                if (stream.Read(head[..2]) != 2)
                {
                    return result;
                }

                int payload = BinaryPrimitives.ReadUInt16BigEndian(head) - 2;
                if (payload < 0 || payload > stream.Length - stream.Position)
                {
                    return result;
                }

                if (b is 0xE1 or 0xED)
                {
                    stream.ReadExactly(buffer, 0, payload);
                    var data = buffer.AsSpan(0, payload);
                    if (b == 0xED)
                    {
                        result = result with { Iptc = result.Iptc || IptcLocationReader.ReadPhotoshop(data) };
                    }
                    else if (data.StartsWith(JpegXmpHeader))
                    {
                        result = result with { Xmp = result.Xmp || XmpLocationScanner.NamesLocation(data[JpegXmpHeader.Length..]) };
                    }
                    else
                    {
                        result = result.Merge(ExifLocationReader.Read(data));
                    }
                }
                else
                {
                    stream.Seek(payload, SeekOrigin.Current);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    private static LocationInfo ScanPng(Stream stream)
    {
        var result = default(LocationInfo);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || !header.SequenceEqual(PngSignature))
        {
            return result;
        }

        for (int chunk = 0; chunk < MaxChunks; chunk++)
        {
            if (stream.Read(header) != 8)
            {
                return result;
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header.Slice(4, 4);
            long dataStart = stream.Position;
            long remaining = stream.Length - dataStart;
            if (length > remaining)
            {
                return result;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                return result;
            }

            bool isExif = type.SequenceEqual("eXIf"u8);
            if (isExif || type.SequenceEqual("iTXt"u8))
            {
                int take = (int)Math.Min(length, isExif ? MaxExifBytes : XmpLocationScanner.MaxScannedBytes + 256);
                var data = new byte[take];
                stream.ReadExactly(data);
                if (isExif)
                {
                    result = result.Merge(ExifLocationReader.Read(data));
                }
                else if (data.AsSpan().StartsWith(PngXmpKeyword) && take > PngXmpKeyword.Length + 2)
                {
                    result = result with { Xmp = result.Xmp || PngXmpNamesLocation(data) };
                }
            }

            stream.Position = dataStart + length + 4; // data + CRC
        }

        return result;
    }

    // iTXt: keyword\0, compression flag, compression method, language\0, translated keyword\0, text (zlib-compressed when the flag is 1).
    private static bool PngXmpNamesLocation(byte[] data)
    {
        int position = PngXmpKeyword.Length;
        bool compressed = data[position] == 1;
        position += 2;
        for (int nulls = 0; nulls < 2; nulls++)
        {
            int end = Array.IndexOf(data, (byte)0, position);
            if (end < 0)
            {
                return false;
            }

            position = end + 1;
        }

        var text = new ReadOnlySpan<byte>(data, position, data.Length - position);
        if (!compressed)
        {
            return XmpLocationScanner.NamesLocation(text);
        }

        using var source = new MemoryStream(data, position, data.Length - position, writable: false);
        using var inflater = new ZLibStream(source, CompressionMode.Decompress);
        var inflated = new byte[XmpLocationScanner.MaxScannedBytes];
        int total = 0;
        try
        {
            int read;
            while (total < inflated.Length && (read = inflater.Read(inflated, total, inflated.Length - total)) > 0)
            {
                total += read;
            }
        }
        catch (InvalidDataException)
        {
            // Keep what inflated before the corruption.
        }

        return XmpLocationScanner.NamesLocation(inflated.AsSpan(0, total));
    }

    private static LocationInfo ScanWebp(Stream stream)
    {
        var result = default(LocationInfo);
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return result;
        }

        for (int chunk = 0; chunk < MaxChunks; chunk++)
        {
            if (stream.Read(header[..8]) != 8)
            {
                return result;
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long dataStart = stream.Position;
            if (length > stream.Length - dataStart)
            {
                return result;
            }

            bool isExif = header[..4].SequenceEqual("EXIF"u8);
            if (isExif || header[..4].SequenceEqual("XMP "u8))
            {
                int take = (int)Math.Min(length, isExif ? MaxExifBytes : XmpLocationScanner.MaxScannedBytes);
                var data = new byte[take];
                stream.ReadExactly(data);
                result = isExif
                    ? result.Merge(ExifLocationReader.Read(data))
                    : result with { Xmp = result.Xmp || XmpLocationScanner.NamesLocation(data) };
            }

            stream.Position = dataStart + length + (length & 1);
        }

        return result;
    }

    private static LocationInfo ScanGif(Stream stream)
    {
        var result = default(LocationInfo);
        Span<byte> header = stackalloc byte[13];
        if (stream.Read(header) != 13 || !header[..4].SequenceEqual("GIF8"u8))
        {
            return result;
        }

        if ((header[10] & 0x80) != 0)
        {
            stream.Seek(3L << ((header[10] & 0x07) + 1), SeekOrigin.Current); // global colour table
        }

        Span<byte> id = stackalloc byte[11];
        Span<byte> descriptor = stackalloc byte[9];
        for (int block = 0; block < MaxGifBlocks; block++)
        {
            int introducer = stream.ReadByte();
            if (introducer == 0x21)
            {
                int label = stream.ReadByte();
                int size = label == 0xFF ? stream.ReadByte() : 0;
                if (size == 11)
                {
                    stream.ReadExactly(id);
                    if (id.SequenceEqual("XMP DataXMP"u8))
                    {
                        result = result with { Xmp = result.Xmp || XmpLocationScanner.NamesLocation(ReadGifXmp(stream)) };
                        continue;
                    }
                }
                else if (size > 0)
                {
                    stream.Seek(size, SeekOrigin.Current);
                }

                SkipGifSubBlocks(stream);
            }
            else if (introducer == 0x2C)
            {
                stream.ReadExactly(descriptor);
                if ((descriptor[8] & 0x80) != 0)
                {
                    stream.Seek(3L << ((descriptor[8] & 0x07) + 1), SeekOrigin.Current); // local colour table
                }

                stream.ReadByte(); // LZW minimum code size
                SkipGifSubBlocks(stream);
            }
            else
            {
                return result; // trailer, end of stream, or garbage
            }
        }

        return result;
    }

    private const int MaxGifBlocks = 2_000_000;

    private static void SkipGifSubBlocks(Stream stream)
    {
        while (true)
        {
            int size = stream.ReadByte();
            if (size <= 0)
            {
                return;
            }

            stream.Seek(size, SeekOrigin.Current);
        }
    }

    // The packet is stored raw; only the first MaxScannedBytes are kept, the rest is walked past as sub-blocks.
    private static byte[] ReadGifXmp(Stream stream)
    {
        using var raw = new MemoryStream();
        Span<byte> chunk = stackalloc byte[255];
        while (true)
        {
            int size = stream.ReadByte();
            if (size <= 0)
            {
                break;
            }

            if (raw.Length < XmpLocationScanner.MaxScannedBytes)
            {
                stream.ReadExactly(chunk[..size]);
                raw.WriteByte((byte)size);
                raw.Write(chunk[..size]);
            }
            else
            {
                stream.Seek(size, SeekOrigin.Current);
            }
        }

        return raw.ToArray();
    }

    private static LocationInfo ScanAvif(Stream stream)
    {
        // The container parse buffers the file (bounded by the decoder's own size limit) but decodes no pixels.
        var metadata = new ImageMetadata();
        Avif.Container.AvifContainerReader.Read(stream, metadata);
        return metadata.LocationFromProfiles();
    }

    private static LocationInfo ScanJxl(Stream stream)
    {
        var container = Jxl.Container.JxlContainer.Parse(Jxl.JxlDecoder.ReadAll(stream));
        var result = container.Exif is { } exif ? ExifLocationReader.Read(exif) : default;
        return container.Xmp is { } xmp ? result with { Xmp = result.Xmp || XmpLocationScanner.NamesLocation(xmp) } : result;
    }

    private static LocationInfo ScanTiff(Stream stream)
    {
        long available = stream.Length - stream.Position;
        var data = new byte[(int)Math.Min(available, MaxTiffBytes)];
        stream.ReadExactly(data);
        return ExifLocationReader.Read(data, isTiffFile: true);
    }
}
