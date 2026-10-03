using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Jpeg.ColorConversion;

/// <summary>
/// SIMD color converter using <see cref="Vector128{T}"/>'s cross-platform generic static API: the BT.601
/// multiply-add chain (see <see cref="ScalarColorConverter"/> for the underlying math) is computed 4 pixels
/// at a time. JITs to SSE2 on x86 and AdvSimd on Arm from one source file.
/// </summary>
/// <remarks>See <see cref="Vector256ColorConverter"/>'s remarks — same widen/narrow approach, 4 lanes instead of 8.</remarks>
internal sealed class Vector128ColorConverter : IColorConverter
{
    private const int Lanes = 4;

    /// <summary>Pixels per iteration of the main loops: one full 16-byte vector per channel.</summary>
    private const int Block = 16;

    private static readonly Vector128<float> V1_402 = Vector128.Create(1.402f);
    private static readonly Vector128<float> V0_344136 = Vector128.Create(0.344136f);
    private static readonly Vector128<float> V0_714136 = Vector128.Create(0.714136f);
    private static readonly Vector128<float> V1_772 = Vector128.Create(1.772f);
    private static readonly Vector128<float> V128 = Vector128.Create(128f);
    private static readonly Vector128<float> VZero = Vector128<float>.Zero;
    private static readonly Vector128<float> V255 = Vector128.Create(255f);
    private static readonly Vector128<float> V0_299 = Vector128.Create(0.299f);
    private static readonly Vector128<float> V0_587 = Vector128.Create(0.587f);
    private static readonly Vector128<float> V0_114 = Vector128.Create(0.114f);
    private static readonly Vector128<float> V0_168736 = Vector128.Create(0.168736f);
    private static readonly Vector128<float> V0_331264 = Vector128.Create(0.331264f);
    private static readonly Vector128<float> V0_5 = Vector128.Create(0.5f);
    private static readonly Vector128<float> V0_418688 = Vector128.Create(0.418688f);
    private static readonly Vector128<float> V0_081312 = Vector128.Create(0.081312f);

    private static readonly Vector128<float> RoundingBias = Vector128.Create(0.5f);

    public void YCbCrToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount)
    {
        int i = 0;
        for (; i + Block <= pixelCount; i += Block)
        {
            YCbCrToRgbBlock(Load(y, i), Load(cb, i), Load(cr, i), out var r, out var g, out var b);
            StoreInterleaved3(rgb, i * 3, r, g, b);
        }

        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = LoadWidened(y, i);
            var cbv = LoadWidened(cb, i) - V128;
            var crv = LoadWidened(cr, i) - V128;

            StoreRounded(Clamp(yv + (V1_402 * crv)), rgb, (i * 3) + 0, 3);
            StoreRounded(Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv)), rgb, (i * 3) + 1, 3);
            StoreRounded(Clamp(yv + (V1_772 * cbv)), rgb, (i * 3) + 2, 3);
        }

        for (; i < pixelCount; i++)
        {
            (byte r, byte g, byte b) = ScalarColorConverter.ConvertYCbCrPixel(y[i], cb[i], cr[i]);
            int offset = i * 3;
            rgb[offset] = r;
            rgb[offset + 1] = g;
            rgb[offset + 2] = b;
        }
    }

    public void YcckToCmyk(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, Span<byte> cmyk, int pixelCount)
    {
        int i = 0;
        for (; i + Block <= pixelCount; i += Block)
        {
            YCbCrToRgbBlock(Load(y, i), Load(cb, i), Load(cr, i), out var r, out var g, out var b);

            // Round R/G/B first, then invert (255 - x on the rounded byte), as in the scalar form.
            var full = Vector128.Create((byte)255);
            StoreInterleaved4(cmyk, i * 4, full - r, full - g, full - b, Load(k, i));
        }

        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = LoadWidened(y, i);
            var cbv = LoadWidened(cb, i) - V128;
            var crv = LoadWidened(cr, i) - V128;

            var rv = Clamp(yv + (V1_402 * crv));
            var gv = Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv));
            var bv = Clamp(yv + (V1_772 * cbv));

            // Round R/G/B first, then invert — see Vector256ColorConverter.YcckToCmyk's remarks.
            StoreRounded(rv, cmyk, (i * 4) + 0, 4, invert: true);
            StoreRounded(gv, cmyk, (i * 4) + 1, 4, invert: true);
            StoreRounded(bv, cmyk, (i * 4) + 2, 4, invert: true);

            for (int lane = 0; lane < Lanes; lane++)
            {
                cmyk[((i + lane) * 4) + 3] = k[i + lane];
            }
        }

        for (; i < pixelCount; i++)
        {
            (byte r, byte g, byte b) = ScalarColorConverter.ConvertYCbCrPixel(y[i], cb[i], cr[i]);
            int offset = i * 4;
            cmyk[offset] = (byte)(255 - r);
            cmyk[offset + 1] = (byte)(255 - g);
            cmyk[offset + 2] = (byte)(255 - b);
            cmyk[offset + 3] = k[i];
        }
    }

    public void RgbToYCbCr(ReadOnlySpan<byte> rgb, Span<byte> y, Span<byte> cb, Span<byte> cr, int pixelCount)
    {
        int i = 0;
        for (; i + Block <= pixelCount; i += Block)
        {
            LoadDeinterleaved3(rgb, i * 3, out var r, out var g, out var b);
            Widen(r, out var r0, out var r1, out var r2, out var r3);
            Widen(g, out var g0, out var g1, out var g2, out var g3);
            Widen(b, out var b0, out var b1, out var b2, out var b3);

            Store(y, i, Pack(
                Clamp((V0_299 * r0) + (V0_587 * g0) + (V0_114 * b0)), Clamp((V0_299 * r1) + (V0_587 * g1) + (V0_114 * b1)),
                Clamp((V0_299 * r2) + (V0_587 * g2) + (V0_114 * b2)), Clamp((V0_299 * r3) + (V0_587 * g3) + (V0_114 * b3))));
            Store(cb, i, Pack(
                Clamp(V128 - (V0_168736 * r0) - (V0_331264 * g0) + (V0_5 * b0)), Clamp(V128 - (V0_168736 * r1) - (V0_331264 * g1) + (V0_5 * b1)),
                Clamp(V128 - (V0_168736 * r2) - (V0_331264 * g2) + (V0_5 * b2)), Clamp(V128 - (V0_168736 * r3) - (V0_331264 * g3) + (V0_5 * b3))));
            Store(cr, i, Pack(
                Clamp(V128 + (V0_5 * r0) - (V0_418688 * g0) - (V0_081312 * b0)), Clamp(V128 + (V0_5 * r1) - (V0_418688 * g1) - (V0_081312 * b1)),
                Clamp(V128 + (V0_5 * r2) - (V0_418688 * g2) - (V0_081312 * b2)), Clamp(V128 + (V0_5 * r3) - (V0_418688 * g3) - (V0_081312 * b3))));
        }

        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var (rv, gv, bv) = LoadWidenedInterleaved(rgb, i);

            StoreRounded(Clamp((V0_299 * rv) + (V0_587 * gv) + (V0_114 * bv)), y, i, 1);
            StoreRounded(Clamp(V128 - (V0_168736 * rv) - (V0_331264 * gv) + (V0_5 * bv)), cb, i, 1);
            StoreRounded(Clamp(V128 + (V0_5 * rv) - (V0_418688 * gv) - (V0_081312 * bv)), cr, i, 1);
        }

        for (; i < pixelCount; i++)
        {
            (y[i], cb[i], cr[i]) = ScalarColorConverter.ConvertRgbPixel(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
        }
    }

    // --- 16-pixel block path -------------------------------------------------------------------------------
    // One full vector per channel in and out: widen to four 4-lane float groups, run the same multiply-add
    // chains as the 4-lane path above (same operations, same order, so output is bit-identical), then narrow
    // back to one byte vector. The interleaved RGB/CMYK sides are byte shuffles, not per-lane scalar copies.

    private static Vector128<byte> Load(ReadOnlySpan<byte> source, int offset)
    {
        _ = source[offset + Block - 1];
        return Vector128.LoadUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), offset));
    }

    private static void Store(Span<byte> destination, int offset, Vector128<byte> value)
    {
        _ = destination[offset + Block - 1];
        value.StoreUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), offset));
    }

    private static void Widen(Vector128<byte> v, out Vector128<float> f0, out Vector128<float> f1, out Vector128<float> f2, out Vector128<float> f3)
    {
        var lo = Vector128.WidenLower(v);
        var hi = Vector128.WidenUpper(v);
        f0 = Vector128.ConvertToSingle(Vector128.WidenLower(lo));
        f1 = Vector128.ConvertToSingle(Vector128.WidenUpper(lo));
        f2 = Vector128.ConvertToSingle(Vector128.WidenLower(hi));
        f3 = Vector128.ConvertToSingle(Vector128.WidenUpper(hi));
    }

    /// <summary>Rounds four already-clamped float groups (as <see cref="ToRoundedInt"/> does) and narrows them to 16 bytes.</summary>
    private static Vector128<byte> Pack(Vector128<float> v0, Vector128<float> v1, Vector128<float> v2, Vector128<float> v3) =>
        Vector128.Narrow(
            Vector128.Narrow(ToRoundedInt(v0).AsUInt32(), ToRoundedInt(v1).AsUInt32()),
            Vector128.Narrow(ToRoundedInt(v2).AsUInt32(), ToRoundedInt(v3).AsUInt32()));

    private static void YCbCrToRgbBlock(Vector128<byte> y, Vector128<byte> cb, Vector128<byte> cr, out Vector128<byte> r, out Vector128<byte> g, out Vector128<byte> b)
    {
        Widen(y, out var y0, out var y1, out var y2, out var y3);
        Widen(cb, out var cb0, out var cb1, out var cb2, out var cb3);
        Widen(cr, out var cr0, out var cr1, out var cr2, out var cr3);
        cb0 -= V128; cb1 -= V128; cb2 -= V128; cb3 -= V128;
        cr0 -= V128; cr1 -= V128; cr2 -= V128; cr3 -= V128;

        r = Pack(Clamp(y0 + (V1_402 * cr0)), Clamp(y1 + (V1_402 * cr1)), Clamp(y2 + (V1_402 * cr2)), Clamp(y3 + (V1_402 * cr3)));
        g = Pack(
            Clamp(y0 - (V0_344136 * cb0) - (V0_714136 * cr0)), Clamp(y1 - (V0_344136 * cb1) - (V0_714136 * cr1)),
            Clamp(y2 - (V0_344136 * cb2) - (V0_714136 * cr2)), Clamp(y3 - (V0_344136 * cb3) - (V0_714136 * cr3)));
        b = Pack(Clamp(y0 + (V1_772 * cb0)), Clamp(y1 + (V1_772 * cb1)), Clamp(y2 + (V1_772 * cb2)), Clamp(y3 + (V1_772 * cb3)));
    }

    /// <summary>
    /// Byte-shuffle tables for packing planar channels into interleaved pixels (<c>Interleave*</c>) and the
    /// reverse (<c>Deinterleave3</c>). <c>table[channel][vector]</c> gives, for each byte of the 16-byte
    /// interleaved vector number <c>vector</c> (or planar channel byte, when deinterleaving), the source byte
    /// to pick from that channel's vector; an index of 255 yields zero, so the per-channel shuffles OR together.
    /// </summary>
    private static readonly Vector128<byte>[][] Interleave3 = BuildTable(channels: 3, deinterleave: false);
    private static readonly Vector128<byte>[][] Deinterleave3 = BuildTable(channels: 3, deinterleave: true);
    private static readonly Vector128<byte>[][] Interleave4 = BuildTable(channels: 4, deinterleave: false);

    private static Vector128<byte>[][] BuildTable(int channels, bool deinterleave)
    {
        // Each of the `channels` interleaved vectors holds 16 bytes; interleaved byte n belongs to pixel
        // n / channels and channel n % channels.
        var table = new Vector128<byte>[channels][];
        for (int c = 0; c < channels; c++)
        {
            table[c] = new Vector128<byte>[channels];
            for (int v = 0; v < channels; v++)
            {
                var idx = new byte[16];
                for (int m = 0; m < 16; m++)
                {
                    if (deinterleave)
                    {
                        // Planar channel c, pixel m, lives at interleaved byte (m * channels) + c; this entry
                        // extracts it from interleaved vector v.
                        int n = (m * channels) + c - (16 * v);
                        idx[m] = n is >= 0 and < 16 ? (byte)n : (byte)255;
                    }
                    else
                    {
                        int n = (16 * v) + m;
                        idx[m] = n % channels == c ? (byte)(n / channels) : (byte)255;
                    }
                }

                table[c][v] = Vector128.LoadUnsafe(ref idx[0]);
            }
        }

        return table;
    }

    private static void StoreInterleaved3(Span<byte> destination, int offset, Vector128<byte> r, Vector128<byte> g, Vector128<byte> b)
    {
        _ = destination[offset + (3 * Block) - 1];
        for (int v = 0; v < 3; v++)
        {
            var packed = Vector128.Shuffle(r, Interleave3[0][v]) | Vector128.Shuffle(g, Interleave3[1][v]) | Vector128.Shuffle(b, Interleave3[2][v]);
            packed.StoreUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), offset + (v * Block)));
        }
    }

    private static void StoreInterleaved4(Span<byte> destination, int offset, Vector128<byte> c0, Vector128<byte> c1, Vector128<byte> c2, Vector128<byte> c3)
    {
        _ = destination[offset + (4 * Block) - 1];
        for (int v = 0; v < 4; v++)
        {
            var packed = Vector128.Shuffle(c0, Interleave4[0][v]) | Vector128.Shuffle(c1, Interleave4[1][v])
                | Vector128.Shuffle(c2, Interleave4[2][v]) | Vector128.Shuffle(c3, Interleave4[3][v]);
            packed.StoreUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), offset + (v * Block)));
        }
    }

    private static void LoadDeinterleaved3(ReadOnlySpan<byte> source, int offset, out Vector128<byte> r, out Vector128<byte> g, out Vector128<byte> b)
    {
        _ = source[offset + (3 * Block) - 1];
        ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetReference(source), offset);
        var v0 = Vector128.LoadUnsafe(ref start);
        var v1 = Vector128.LoadUnsafe(ref start, Block);
        var v2 = Vector128.LoadUnsafe(ref start, 2 * Block);

        r = Vector128.Shuffle(v0, Deinterleave3[0][0]) | Vector128.Shuffle(v1, Deinterleave3[0][1]) | Vector128.Shuffle(v2, Deinterleave3[0][2]);
        g = Vector128.Shuffle(v0, Deinterleave3[1][0]) | Vector128.Shuffle(v1, Deinterleave3[1][1]) | Vector128.Shuffle(v2, Deinterleave3[1][2]);
        b = Vector128.Shuffle(v0, Deinterleave3[2][0]) | Vector128.Shuffle(v1, Deinterleave3[2][1]) | Vector128.Shuffle(v2, Deinterleave3[2][2]);
    }

    private static Vector128<float> LoadWidened(ReadOnlySpan<byte> source, int offset)
    {
        Span<byte> padded = stackalloc byte[16];
        source.Slice(offset, Lanes).CopyTo(padded);
        return WidenBytes(padded);
    }

    private static (Vector128<float> R, Vector128<float> G, Vector128<float> B) LoadWidenedInterleaved(ReadOnlySpan<byte> rgb, int i)
    {
        Span<byte> rPadded = stackalloc byte[16];
        Span<byte> gPadded = stackalloc byte[16];
        Span<byte> bPadded = stackalloc byte[16];
        for (int lane = 0; lane < Lanes; lane++)
        {
            int offset = (i + lane) * 3;
            rPadded[lane] = rgb[offset];
            gPadded[lane] = rgb[offset + 1];
            bPadded[lane] = rgb[offset + 2];
        }

        return (WidenBytes(rPadded), WidenBytes(gPadded), WidenBytes(bPadded));
    }

    /// <summary><paramref name="padded16"/>'s first <see cref="Lanes"/> bytes widened to a <see cref="Vector128{Single}"/> via byte-&gt;ushort-&gt;uint widen then a hardware uint-&gt;float convert — the rest of the 16 bytes is don't-care padding, never read past lane 3.</summary>
    private static Vector128<float> WidenBytes(ReadOnlySpan<byte> padded16)
    {
        var byteVec = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(padded16));
        var ushortVec = Vector128.WidenLower(byteVec);
        var uintVec = Vector128.WidenLower(ushortVec);
        return Vector128.ConvertToSingle(uintVec);
    }

    private static Vector128<int> ToRoundedInt(Vector128<float> value) => Vector128.ConvertToInt32(value + RoundingBias);

    /// <summary>
    /// Rounds <paramref name="value"/> and writes the <see cref="Lanes"/> resulting bytes into
    /// <paramref name="destination"/> at the given <paramref name="stride"/>. See
    /// <see cref="Vector256ColorConverter.StoreRounded"/>'s remarks: only the planar (<c>stride == 1</c>,
    /// no invert) case is worth narrowing via the hardware int-&gt;uint-&gt;ushort-&gt;byte chain for a
    /// genuine vector store — measured slower, not faster, for the interleaved/invert cases where the store
    /// stays a scalar loop regardless, so those keep the plain <c>stackalloc int[]</c> + scalar-cast path.
    /// </summary>
    private static void StoreRounded(Vector128<float> value, Span<byte> destination, int firstOffset, int stride, bool invert = false)
    {
        if (stride == 1 && !invert)
        {
            var asUInt = ToRoundedInt(value).AsUInt32();
            var narrowedToUShort = Vector128.Narrow(asUInt, Vector128<uint>.Zero);
            var bytes = Vector128.Narrow(narrowedToUShort, Vector128<ushort>.Zero).GetLower();
            Span<byte> lane8 = stackalloc byte[8];
            bytes.StoreUnsafe(ref lane8[0]);
            lane8[..Lanes].CopyTo(destination.Slice(firstOffset, Lanes));
            return;
        }

        Span<int> rounded = stackalloc int[Lanes];
        ToRoundedInt(value).StoreUnsafe(ref rounded[0]);
        for (int i = 0; i < Lanes; i++)
        {
            destination[firstOffset + (i * stride)] = (byte)(invert ? 255 - rounded[i] : rounded[i]);
        }
    }

    private static Vector128<float> Clamp(Vector128<float> value) => Vector128.Min(Vector128.Max(value, VZero), V255);
}
