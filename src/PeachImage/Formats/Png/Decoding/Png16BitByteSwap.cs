using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>
/// Converts a row of big-endian 16-bit PNG samples to native-endian <see cref="ushort"/>s. Every .NET
/// target this repo runs on (x64/Arm64/x86) is little-endian, so this is exactly a byte-pair swap
/// (reverse each adjacent 2-byte pair) — a fixed <see cref="Vector128{T}"/> shuffle mask, following the
/// same convention as <c>PixelFormatConversionKernels</c>'s shuffle-based kernels.
/// </summary>
internal static class Png16BitByteSwap
{
    // Reverses each adjacent byte pair within a 16-byte vector (8 ushorts): byte i <-> byte i^1. Period-2,
    // so this same mask (repeated) would also be valid as a Vector256 shuffle if a 256-bit tier is ever added.
    private static readonly Vector128<byte> SwapPairsShuffle =
        Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);

    /// <summary>
    /// Reads big-endian 16-bit samples from <paramref name="bigEndianBytes"/> and writes them as
    /// native-endian <see cref="ushort"/>s into <paramref name="destination"/> (same byte length as the
    /// source). Both spans' lengths must be even.
    /// </summary>
    public static void SwapBigEndian16(ReadOnlySpan<byte> bigEndianBytes, Span<byte> destination)
    {
        int i = 0;
        if (BitConverter.IsLittleEndian && Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= bigEndianBytes.Length; i += 16)
            {
                var v = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(bigEndianBytes.Slice(i, 16)));
                Vector128.Shuffle(v, SwapPairsShuffle).StoreUnsafe(ref destination[i]);
            }
        }

        var destUShorts = MemoryMarshal.Cast<byte, ushort>(destination[i..]);
        for (int pairIndex = 0; i < bigEndianBytes.Length; i += 2, pairIndex++)
        {
            destUShorts[pairIndex] = BinaryPrimitives.ReadUInt16BigEndian(bigEndianBytes.Slice(i, 2));
        }
    }
}
