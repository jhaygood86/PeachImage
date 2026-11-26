using System.Buffers.Binary;
using System.IO.Compression;

namespace PeachImage.Formats.Shared.Metadata;

/// <summary>
/// Reads the color declarations of a PNG, JPEG or WebP file (its ICC profile and, for PNG, its <c>cICP</c> code points) from the container
/// structure alone, without decoding any pixels, so <see cref="Image.Identify(Stream)"/> can report the color space cheaply.
/// </summary>
internal static class ColorInfoScanner
{
    private const int MaxProfileBytes = 16 << 20;
    private const int MaxChunks = 4096;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool Supports(string formatName) => formatName is "png" or "jpeg" or "webp";

    public static (byte[]? Icc, CicpCode? Cicp) Scan(Stream stream, string formatName)
    {
        long start = stream.Position;
        try
        {
            return formatName switch
            {
                "png" => ScanPng(stream),
                "jpeg" => (ScanJpeg(stream), null),
                "webp" => (ScanWebp(stream), null),
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

    private static (byte[]? Icc, CicpCode? Cicp) ScanPng(Stream stream)
    {
        Span<byte> signature = stackalloc byte[8];
        if (stream.Read(signature) != 8 || !((ReadOnlySpan<byte>)signature).SequenceEqual(PngSignature))
        {
            return default;
        }

        byte[]? icc = null;
        CicpCode? cicp = null;
        Span<byte> header = stackalloc byte[8];
        for (int chunks = 0; chunks < MaxChunks; chunks++)
        {
            if (stream.Read(header) != 8)
            {
                break;
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header.Slice(4, 4);
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                break;
            }

            if ((type.SequenceEqual("iCCP"u8) || type.SequenceEqual("cICP"u8)) && length <= MaxProfileBytes)
            {
                var data = new byte[length];
                stream.ReadExactly(data);
                if (type[0] == (byte)'c' && data.Length == 4)
                {
                    cicp = new CicpCode(data[0], data[1], data[2], data[3] != 0);
                }
                else if (type[0] == (byte)'i')
                {
                    icc = InflateIccp(data);
                }

                stream.Seek(4, SeekOrigin.Current); // CRC
            }
            else
            {
                stream.Seek((long)length + 4, SeekOrigin.Current);
            }
        }

        return (icc, cicp);
    }

    // iCCP: profile name, NUL, compression method (0), then zlib data.
    private static byte[]? InflateIccp(byte[] data)
    {
        int nul = Array.IndexOf(data, (byte)0);
        if (nul < 1 || nul + 2 > data.Length || data[nul + 1] != 0)
        {
            return null;
        }

        using var input = new MemoryStream(data, nul + 2, data.Length - nul - 2);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = zlib.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxProfileBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static byte[]? ScanJpeg(Stream stream)
    {
        Span<byte> head = stackalloc byte[4];
        if (stream.Read(head[..2]) != 2 || head[0] != 0xFF || head[1] != 0xD8)
        {
            return null;
        }

        SortedDictionary<int, byte[]>? parts = null;
        int expected = 0;
        for (int segments = 0; segments < MaxChunks; segments++)
        {
            // Find the next marker.
            int b = stream.ReadByte();
            while (b != 0xFF)
            {
                if (b < 0)
                {
                    return Assemble(parts, expected);
                }

                b = stream.ReadByte();
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);

            if (marker < 0 || marker == 0xDA || marker == 0xD9)
            {
                break; // start of scan or end of image: all headers are behind us
            }

            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
            {
                continue; // markers without a length
            }

            if (stream.Read(head[..2]) != 2)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(head) - 2;
            if (length < 0)
            {
                break;
            }

            if (marker == 0xE2 && length >= 14)
            {
                var data = new byte[length];
                stream.ReadExactly(data);
                if (data.AsSpan(0, 12).SequenceEqual("ICC_PROFILE\0"u8))
                {
                    parts ??= [];
                    parts[data[12]] = data[14..];
                    expected = data[13];
                }
            }
            else
            {
                stream.Seek(length, SeekOrigin.Current);
            }
        }

        return Assemble(parts, expected);
    }

    private static byte[]? Assemble(SortedDictionary<int, byte[]>? parts, int expected)
    {
        if (parts is null || parts.Count == 0 || parts.Count != expected)
        {
            return null;
        }

        long total = 0;
        foreach (var part in parts.Values)
        {
            total += part.Length;
        }

        if (total > MaxProfileBytes)
        {
            return null;
        }

        var result = new byte[total];
        int offset = 0;
        foreach (var part in parts.Values)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[]? ScanWebp(Stream stream)
    {
        Span<byte> riff = stackalloc byte[12];
        if (stream.Read(riff) != 12 || !riff[..4].SequenceEqual("RIFF"u8) || !riff.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        Span<byte> header = stackalloc byte[8];
        for (int chunks = 0; chunks < MaxChunks; chunks++)
        {
            if (stream.Read(header) != 8)
            {
                break;
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (header[..4].SequenceEqual("ICCP"u8))
            {
                if (length > MaxProfileBytes)
                {
                    return null;
                }

                var data = new byte[length];
                stream.ReadExactly(data);
                return data;
            }

            if (header[..4].SequenceEqual("VP8 "u8) || header[..4].SequenceEqual("VP8L"u8) || header[..4].SequenceEqual("ANMF"u8))
            {
                break; // the profile chunk always precedes the image data
            }

            stream.Seek((long)length + (length & 1), SeekOrigin.Current);
        }

        return null;
    }
}
