using System.Buffers.Binary;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding;

/// <summary>
/// Low-level RIFF chunk-framing writer, the mirror image of <see cref="Webp.Decoding.WebpChunkReader"/>:
/// FourCC + little-endian uint32 size + payload + an even-byte pad. Used by <see cref="WebpImageEncoder"/>
/// to stream a whole WebP file out in one forward pass once every chunk's payload is already known.
/// </summary>
internal static class WebpContainerWriter
{
    private const int Vp8XPayloadSize = 10;

    /// <summary>Writes the 12-byte <c>RIFF</c>/size/<c>WEBP</c> header. <paramref name="riffSize"/> is everything after this field — i.e. 4 (for "WEBP") plus every chunk's total framed size.</summary>
    public static void WriteRiffHeader(Stream stream, uint riffSize)
    {
        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'R';
        header[1] = (byte)'I';
        header[2] = (byte)'F';
        header[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], riffSize);
        header[8] = (byte)'W';
        header[9] = (byte)'E';
        header[10] = (byte)'B';
        header[11] = (byte)'P';
        stream.Write(header);
    }

    /// <summary>Writes one chunk: 4-byte FourCC, little-endian uint32 payload length (unpadded), the payload itself, then a single zero pad byte if the payload's length is odd.</summary>
    public static void WriteChunk(Stream stream, string fourCc, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        header[0] = (byte)fourCc[0];
        header[1] = (byte)fourCc[1];
        header[2] = (byte)fourCc[2];
        header[3] = (byte)fourCc[3];
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], (uint)payload.Length);
        stream.Write(header);
        stream.Write(payload);

        if ((payload.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    /// <summary>The total framed size a chunk with this payload length will occupy: 8-byte header + payload + even-byte pad.</summary>
    public static long GetFramedChunkSize(int payloadLength) => 8L + payloadLength + (payloadLength & 1);

    /// <summary>
    /// Builds the 10-byte <c>VP8X</c> payload: flags byte (alpha/ICC/EXIF/XMP presence, animation always
    /// unset), 3 reserved zero bytes, then 24-bit little-endian <c>width-1</c>/<c>height-1</c> — the exact
    /// inverse of <see cref="Webp.Decoding.WebpContainerReader"/>'s <c>ParseVp8X</c>.
    /// </summary>
    public static byte[] BuildVp8XPayload(int width, int height, bool hasAlpha, bool hasIcc, bool hasExif, bool hasXmp)
    {
        byte[] payload = new byte[Vp8XPayloadSize];

        byte flags = 0;
        if (hasIcc)
        {
            flags |= Vp8XFlags.IccBit;
        }

        if (hasAlpha)
        {
            flags |= Vp8XFlags.AlphaBit;
        }

        if (hasExif)
        {
            flags |= Vp8XFlags.ExifBit;
        }

        if (hasXmp)
        {
            flags |= Vp8XFlags.XmpBit;
        }

        payload[0] = flags;

        int widthMinusOne = width - 1;
        payload[4] = (byte)widthMinusOne;
        payload[5] = (byte)(widthMinusOne >> 8);
        payload[6] = (byte)(widthMinusOne >> 16);

        int heightMinusOne = height - 1;
        payload[7] = (byte)heightMinusOne;
        payload[8] = (byte)(heightMinusOne >> 8);
        payload[9] = (byte)(heightMinusOne >> 16);

        return payload;
    }
}
