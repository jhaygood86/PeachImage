using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PeachImage.Formats.Png.Encoding;

/// <summary>
/// Packs source pixel bytes into PNG's on-disk sample layout. PeachImage's own 16-bit pixel formats
/// (<see cref="PixelFormat.Gray16"/>/<see cref="PixelFormat.Rgb48"/>/<see cref="PixelFormat.Rgba64"/>)
/// store samples as native-endian <see cref="ushort"/>s in memory, but PNG requires big-endian —
/// this is the only real transformation encoding needs (v1 never encodes to a bit depth PeachImage
/// doesn't already natively hold, so no sub-8-bit packing is needed here, unlike decode's unpacker).
/// </summary>
internal static class PngBitPacker
{
    /// <summary>Copies one row of 8-bit-per-sample source bytes straight through (PNG's channel order already matches PeachImage's).</summary>
    public static void PackRow8(ReadOnlySpan<byte> sourceRow, Span<byte> destination) => sourceRow.CopyTo(destination);

    /// <summary>Byte-swaps one row of native-endian 16-bit samples into PNG's required big-endian order.</summary>
    public static void PackRow16(ReadOnlySpan<byte> sourceRow, Span<byte> destination)
    {
        var samples = MemoryMarshal.Cast<byte, ushort>(sourceRow);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(i * 2, 2), samples[i]);
        }
    }
}
