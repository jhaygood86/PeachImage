using System.Buffers.Binary;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>
/// Reads the <c>tRNS</c> chunk for color types 0 (grayscale) and 2 (truecolor): a single unscaled
/// sample-value "key" marking one exact color as fully transparent (chroma-key semantics, distinct
/// from color type 3's per-palette-entry alpha, which <see cref="PngPalette"/> handles instead).
/// </summary>
internal static class PngTrnsReader
{
    /// <summary>Reads the transparency key's unscaled sample value(s): 1 for grayscale, 3 (R,G,B) for truecolor.</summary>
    public static ushort[] ReadKey(byte[] data, PngColorType colorType)
    {
        switch (colorType)
        {
            case PngColorType.Grayscale:
                if (data.Length != 2)
                {
                    throw new PngDecodingException($"tRNS chunk for grayscale has an invalid length of {data.Length} bytes (expected 2).");
                }

                return [BinaryPrimitives.ReadUInt16BigEndian(data)];

            case PngColorType.Truecolor:
                if (data.Length != 6)
                {
                    throw new PngDecodingException($"tRNS chunk for truecolor has an invalid length of {data.Length} bytes (expected 6).");
                }

                return
                [
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2)),
                ];

            default:
                throw new PngDecodingException($"tRNS chunk is not valid for color type {(byte)colorType}.");
        }
    }
}
