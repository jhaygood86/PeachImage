using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Unpacks a decompressed, still bit-depth-packed row into per-pixel samples, widened to <see cref="ushort"/>
/// regardless of source bit depth and left <em>unscaled</em> (raw 0..2^bitDepth-1 magnitudes, or raw palette
/// indices) — mirrors <c>Png.Decoding.PngBitUnpacker</c>'s shift/mask logic for 1/2/4/8-bit samples,
/// MSB-first within each byte (TIFF 6.0 §7 "Bit Order" uses the same convention PNG does), extended to honor
/// the file's own <see cref="TiffByteOrder"/> for 16-bit samples rather than PNG's fixed big-endian. Scaling
/// unscaled samples to a display range (WhiteIsZero inversion, sub-8-bit bit-replication) is
/// <see cref="TiffSampleWriter"/>'s job, not this one — same separation PNG's own bit unpacker keeps.
/// </summary>
/// <remarks>
/// The 8- and 16-bit paths are the ones a real scanner/export-tool TIFF actually hits (1/2/4-bit are the
/// rarer bilevel/low-color case), so those two get <see cref="Vector256{T}"/>/<see cref="Vector128{T}"/>
/// fast paths; the 1/2/4-bit paths stay scalar — each output sample's shift amount and source byte both
/// depend on its position mod (8/bitDepth), which doesn't map onto a fixed shuffle/shift mask the way the
/// byte- and word-aligned cases do, mirroring this codebase's existing calls not to force SIMD onto a loop
/// shaped like that (e.g. <c>GifLzwDecoder</c>'s chain walk).
/// </remarks>
internal static class TiffBitUnpacker
{
    /// <summary>Unpacks <paramref name="packedRow"/> (bit depth 1/2/4/8/16) into <paramref name="samplesOut"/> (length must be <paramref name="sampleCount"/>).</summary>
    public static void Unpack(ReadOnlySpan<byte> packedRow, int bitDepth, int sampleCount, TiffByteOrder byteOrder, Span<ushort> samplesOut)
    {
        switch (bitDepth)
        {
            case 16:
                UnpackWord(packedRow, sampleCount, byteOrder, samplesOut);
                return;

            case 8:
                UnpackByte(packedRow, sampleCount, samplesOut);
                return;

            case 4:
            case 2:
            case 1:
                UnpackSubByte(packedRow, bitDepth, sampleCount, samplesOut);
                return;

            default:
                throw new TiffDecodingException($"Unsupported TIFF bit depth {bitDepth}.");
        }
    }

    /// <summary>Every .NET target this repo runs on (x64/Arm64/x86) is little-endian, so a little-endian-file row is already exactly the native <see cref="ushort"/> layout (a plain reinterpret+copy, itself BCL-vectorized); only a big-endian ('MM') file needs an actual byte-pair swap.</summary>
    private static void UnpackWord(ReadOnlySpan<byte> packedRow, int sampleCount, TiffByteOrder byteOrder, Span<ushort> samplesOut)
    {
        var wordRow = packedRow[..(sampleCount * 2)];

        if (byteOrder == TiffByteOrder.LittleEndian)
        {
            MemoryMarshal.Cast<byte, ushort>(wordRow).CopyTo(samplesOut);
            return;
        }

        SwapBigEndian16(wordRow, samplesOut);
    }

    // Reverses each adjacent byte pair within a 16-byte vector (8 ushorts): byte i <-> byte i^1. Exactly the
    // same shuffle Png16BitByteSwap uses for its (always-big-endian) PNG rows. Deliberately Vector128 only,
    // not also Vector256: measured a Vector256 tier using this same mask replicated across both 128-bit
    // lanes and it produced wrong output for the *second* lane -- .NET's portable Vector256.Shuffle does not
    // guarantee AVX2 vpshufb's per-128-bit-lane semantics the way raw platform intrinsics would, so indices
    // valid for one lane aren't safe to reuse verbatim for the other. Matches this codebase's existing
    // documented call not to reach for Vector256 on shuffle-based kernels (see
    // PixelFormatConversionKernels's remarks on AVX2's vpshufb not crossing the 128-bit lane boundary).
    private static readonly Vector128<byte> SwapPairsShuffle128 =
        Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);

    private static void SwapBigEndian16(ReadOnlySpan<byte> bigEndianBytes, Span<ushort> samplesOut)
    {
        var destBytes = MemoryMarshal.Cast<ushort, byte>(samplesOut);
        int i = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= bigEndianBytes.Length; i += 16)
            {
                var v = Vector128.LoadUnsafe(in bigEndianBytes[i]);
                Vector128.Shuffle(v, SwapPairsShuffle128).StoreUnsafe(ref destBytes[i]);
            }
        }

        for (; i < bigEndianBytes.Length; i += 2)
        {
            destBytes[i] = bigEndianBytes[i + 1];
            destBytes[i + 1] = bigEndianBytes[i];
        }
    }

    /// <summary>Zero-extends each byte to a <see cref="ushort"/> (not the bit-replicating widen this codebase's <c>PixelFormatConversionKernels.WidenBytesToUInt16</c> does — that widens a channel's <em>value range</em> for pixel-format conversion; this widens only the storage width of an already-8-bit-range raw sample).</summary>
    private static void UnpackByte(ReadOnlySpan<byte> packedRow, int sampleCount, Span<ushort> samplesOut)
    {
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= sampleCount; i += 32)
            {
                var bytes = Vector256.LoadUnsafe(in packedRow[i]);
                var (lower, upper) = Vector256.Widen(bytes);
                lower.StoreUnsafe(ref samplesOut[i]);
                upper.StoreUnsafe(ref samplesOut[i + 16]);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= sampleCount; i += 16)
            {
                var bytes = Vector128.LoadUnsafe(in packedRow[i]);
                Vector128.WidenLower(bytes).StoreUnsafe(ref samplesOut[i]);
                Vector128.WidenUpper(bytes).StoreUnsafe(ref samplesOut[i + 8]);
            }
        }

        for (; i < sampleCount; i++)
        {
            samplesOut[i] = packedRow[i];
        }
    }

    private static void UnpackSubByte(ReadOnlySpan<byte> packedRow, int bitDepth, int sampleCount, Span<ushort> samplesOut)
    {
        int samplesPerByte = 8 / bitDepth;
        byte mask = (byte)((1 << bitDepth) - 1);

        for (int i = 0; i < sampleCount; i++)
        {
            int byteIndex = i / samplesPerByte;
            int sampleInByte = i % samplesPerByte;
            int shift = 8 - bitDepth - (sampleInByte * bitDepth);
            samplesOut[i] = (ushort)((packedRow[byteIndex] >> shift) & mask);
        }
    }
}
