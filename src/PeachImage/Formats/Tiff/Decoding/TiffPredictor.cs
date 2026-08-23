using System.Buffers.Binary;

namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Undoes TIFF's Predictor=2 (horizontal differencing): each sample was encoded as the difference from the
/// sample <c>samplesPerPixel</c> positions before it in the same row (i.e. the same channel of the
/// previous pixel), so reconstruction is a running sum per channel, left to right. Applied to a still
/// bit-depth-packed row — Predictor is only supported at 8- and 16-bit depths (validated by
/// <see cref="TiffValidation"/>), both byte-aligned, so this operates directly on the decompressed row bytes
/// rather than needing <see cref="TiffBitUnpacker"/> first.
/// </summary>
internal static class TiffPredictor
{
    /// <summary>Reverses horizontal differencing on an 8-bit-per-sample row, in place.</summary>
    public static void UndoHorizontalDifferencing8(Span<byte> row, int samplesPerPixel)
    {
        for (int i = samplesPerPixel; i < row.Length; i++)
        {
            row[i] = (byte)(row[i] + row[i - samplesPerPixel]);
        }
    }

    /// <summary>Reverses horizontal differencing on a 16-bit-per-sample row (raw file-byte-order bytes), in place.</summary>
    public static void UndoHorizontalDifferencing16(Span<byte> row, int samplesPerPixel, TiffByteOrder byteOrder)
    {
        int sampleStrideBytes = samplesPerPixel * 2;

        for (int offset = sampleStrideBytes; offset + 2 <= row.Length; offset += 2)
        {
            var current = row.Slice(offset, 2);
            var previous = row.Slice(offset - sampleStrideBytes, 2);

            ushort currentValue = byteOrder == TiffByteOrder.LittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(current)
                : BinaryPrimitives.ReadUInt16BigEndian(current);
            ushort previousValue = byteOrder == TiffByteOrder.LittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(previous)
                : BinaryPrimitives.ReadUInt16BigEndian(previous);

            ushort reconstructed = (ushort)(currentValue + previousValue);

            if (byteOrder == TiffByteOrder.LittleEndian)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(current, reconstructed);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(current, reconstructed);
            }
        }
    }
}
