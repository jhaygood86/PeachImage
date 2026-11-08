using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Jpeg.ColorConversion;

/// <summary>
/// SIMD color converter using <see cref="Vector256{T}"/>'s cross-platform generic static API: the BT.601
/// multiply-add chain is computed 8 pixels at a time. Selected only when
/// <see cref="Vector256.IsHardwareAccelerated"/> (effectively: AVX/AVX2 present).
/// </summary>
/// <remarks>
/// <see cref="LoadWidened"/>/<see cref="ToRoundedInt"/> convert bytes&lt;-&gt;floats via hardware widen/convert
/// instructions rather than a scalar per-lane cast loop. The strided gather (reading interleaved RGB into
/// separate R/G/B lanes in <see cref="RgbToYCbCr"/>) and scatter (writing interleaved RGB/CMYK out of
/// separate lanes in <see cref="YCbCrToRgb"/>/<see cref="YcckToCmyk"/>) stay scalar — the source/destination
/// layout is array-of-structs, not a contiguous run, so there's no vector load/store that fits it directly.
/// </remarks>
internal sealed class Vector256ColorConverter : IColorConverter
{
    private const int Lanes = 8;

    private static readonly Vector256<float> V1_402 = Vector256.Create(1.402f);
    private static readonly Vector256<float> V0_344136 = Vector256.Create(0.344136f);
    private static readonly Vector256<float> V0_714136 = Vector256.Create(0.714136f);
    private static readonly Vector256<float> V1_772 = Vector256.Create(1.772f);
    private static readonly Vector256<float> V128 = Vector256.Create(128f);
    private static readonly Vector256<float> VZero = Vector256<float>.Zero;
    private static readonly Vector256<float> V255 = Vector256.Create(255f);
    private static readonly Vector256<float> V0_299 = Vector256.Create(0.299f);
    private static readonly Vector256<float> V0_587 = Vector256.Create(0.587f);
    private static readonly Vector256<float> V0_114 = Vector256.Create(0.114f);
    private static readonly Vector256<float> V0_168736 = Vector256.Create(0.168736f);
    private static readonly Vector256<float> V0_331264 = Vector256.Create(0.331264f);
    private static readonly Vector256<float> V0_5 = Vector256.Create(0.5f);
    private static readonly Vector256<float> V0_418688 = Vector256.Create(0.418688f);
    private static readonly Vector256<float> V0_081312 = Vector256.Create(0.081312f);

    // Every value this converter rounds is already Clamp()-ed to [0, 255] first, so "add 0.5 and truncate
    // toward zero" (a plain ConvertToInt32) is exact round-half-up with no negative-value edge case to
    // worry about — the same bias-and-truncate trick used by the DCT kernels, replacing a MathF.Round call.
    private static readonly Vector256<float> RoundingBias = Vector256.Create(0.5f);

    // Byte-shuffle indices for packing 8 lanes of separate R/G/B bytes (each vector's low 8 bytes hold the
    // data, high 8 bytes are zero — see NarrowRounded) into 24 bytes of interleaved r,g,b,r,g,b,... output,
    // avoiding a scalar per-byte store loop. Index 8 always selects a known-zero byte from the source
    // vector's upper half, so out-of-channel positions contribute nothing to the OR below. Reg0 covers
    // output bytes 0-15 (pixels 0-4 plus pixel 5's R byte); Reg1's low 8 bytes cover output bytes 16-23
    // (pixel 5's G/B through pixel 7).
    private static readonly Vector128<byte> RgbShuffleR0 = Vector128.Create((byte)0, 8, 8, 1, 8, 8, 2, 8, 8, 3, 8, 8, 4, 8, 8, 5);
    private static readonly Vector128<byte> RgbShuffleG0 = Vector128.Create((byte)8, 0, 8, 8, 1, 8, 8, 2, 8, 8, 3, 8, 8, 4, 8, 8);
    private static readonly Vector128<byte> RgbShuffleB0 = Vector128.Create((byte)8, 8, 0, 8, 8, 1, 8, 8, 2, 8, 8, 3, 8, 8, 4, 8);
    private static readonly Vector128<byte> RgbShuffleR1 = Vector128.Create((byte)8, 8, 6, 8, 8, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8);
    private static readonly Vector128<byte> RgbShuffleG1 = Vector128.Create((byte)5, 8, 8, 6, 8, 8, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8);
    private static readonly Vector128<byte> RgbShuffleB1 = Vector128.Create((byte)8, 5, 8, 8, 6, 8, 8, 7, 8, 8, 8, 8, 8, 8, 8, 8);

    // Output-byte index tables for expanding planar R, G, B (16 bytes each) into three 16-byte runs of packed RGB:
    // output byte k of run j holds channel (k + 16j) % 3 of pixel (k + 16j) / 3; 0xFF zeroes the lane.
    private static readonly Vector128<byte>[] Rgb16Shuffle = BuildRgb16Shuffles();

    private static Vector128<byte>[] BuildRgb16Shuffles()
    {
        var tables = new Vector128<byte>[9];
        Span<byte> indices = stackalloc byte[16];
        for (int run = 0; run < 3; run++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                for (int k = 0; k < 16; k++)
                {
                    int global = (run * 16) + k;
                    indices[k] = global % 3 == channel ? (byte)(global / 3) : (byte)0xFF;
                }

                tables[(run * 3) + channel] = Vector128.Create<byte>(indices);
            }
        }

        return tables;
    }

    /// <summary>
    /// Sixteen pixels per iteration: samples widen straight from memory to 32-bit lanes, the same float arithmetic
    /// as the eight-lane loop below runs on two vectors, and the results are packed to bytes and interleaved with
    /// three byte shuffles per output vector. Returns the number of pixels converted.
    /// </summary>
    private static unsafe int YCbCrToRgbAvx2(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount)
    {
        if (y.Length < pixelCount || cb.Length < pixelCount || cr.Length < pixelCount || rgb.Length < pixelCount * 3)
        {
            throw new ArgumentException("Row buffers are shorter than the pixel count.");
        }

        var shuffle = Rgb16Shuffle;
        Vector128<byte> r0 = shuffle[0], g0 = shuffle[1], b0 = shuffle[2];
        Vector128<byte> r1 = shuffle[3], g1 = shuffle[4], b1 = shuffle[5];
        Vector128<byte> r2 = shuffle[6], g2 = shuffle[7], b2 = shuffle[8];

        int i = 0;
        fixed (byte* py = y, pcb = cb, pcr = cr, pd = rgb)
        {
            for (; i + 16 <= pixelCount; i += 16)
            {
                var yLo = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(py + i));
                var yHi = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(py + i + 8));
                var cbLo = Avx.Subtract(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(pcb + i)), V128);
                var cbHi = Avx.Subtract(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(pcb + i + 8)), V128);
                var crLo = Avx.Subtract(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(pcr + i)), V128);
                var crHi = Avx.Subtract(Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(pcr + i + 8)), V128);

                var red = PackChannel(
                    ClampRound(Avx.Add(yLo, Avx.Multiply(V1_402, crLo))),
                    ClampRound(Avx.Add(yHi, Avx.Multiply(V1_402, crHi))));
                var green = PackChannel(
                    ClampRound(Avx.Subtract(Avx.Subtract(yLo, Avx.Multiply(V0_344136, cbLo)), Avx.Multiply(V0_714136, crLo))),
                    ClampRound(Avx.Subtract(Avx.Subtract(yHi, Avx.Multiply(V0_344136, cbHi)), Avx.Multiply(V0_714136, crHi))));
                var blue = PackChannel(
                    ClampRound(Avx.Add(yLo, Avx.Multiply(V1_772, cbLo))),
                    ClampRound(Avx.Add(yHi, Avx.Multiply(V1_772, cbHi))));

                byte* dst = pd + (i * 3);
                Sse2.Store(dst, Sse2.Or(Sse2.Or(Ssse3.Shuffle(red, r0), Ssse3.Shuffle(green, g0)), Ssse3.Shuffle(blue, b0)));
                Sse2.Store(dst + 16, Sse2.Or(Sse2.Or(Ssse3.Shuffle(red, r1), Ssse3.Shuffle(green, g1)), Ssse3.Shuffle(blue, b1)));
                Sse2.Store(dst + 32, Sse2.Or(Sse2.Or(Ssse3.Shuffle(red, r2), Ssse3.Shuffle(green, g2)), Ssse3.Shuffle(blue, b2)));
            }
        }

        return i;
    }

    /// <summary>Clamp to [0,255], add 0.5 and truncate: the exact round-half-up the eight-lane path uses.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ClampRound(Vector256<float> value) =>
        Avx.ConvertToVector256Int32WithTruncation(Avx.Add(Avx.Min(Avx.Max(value, VZero), V255), RoundingBias));

    /// <summary>Packs two vectors of eight 0..255 ints into sixteen bytes in lane order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> PackChannel(Vector256<int> low, Vector256<int> high)
    {
        // packssdw interleaves the two inputs per 128-bit half; the 64-bit permute restores low-then-high order.
        var packed = Avx2.Permute4x64(Avx2.PackSignedSaturate(low, high).AsInt64(), 0xD8).AsInt16();
        return Sse2.PackUnsignedSaturate(packed.GetLower(), packed.GetUpper());
    }

    public void YCbCrToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount)
    {
        int i = Avx2.IsSupported && Ssse3.IsSupported ? YCbCrToRgbAvx2(y, cb, cr, rgb, pixelCount) : 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = LoadWidened(y, i);
            var cbv = LoadWidened(cb, i) - V128;
            var crv = LoadWidened(cr, i) - V128;

            var rv = Clamp(yv + (V1_402 * crv));
            var gv = Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv));
            var bv = Clamp(yv + (V1_772 * cbv));

            StoreInterleavedRgb(rv, gv, bv, rgb, i * 3);
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
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var yv = LoadWidened(y, i);
            var cbv = LoadWidened(cb, i) - V128;
            var crv = LoadWidened(cr, i) - V128;

            var rv = Clamp(yv + (V1_402 * crv));
            var gv = Clamp(yv - (V0_344136 * cbv) - (V0_714136 * crv));
            var bv = Clamp(yv + (V1_772 * cbv));

            // Round R/G/B first (matching ScalarColorConverter's byte-then-invert order exactly), then
            // invert — inverting the float before rounding can round the other way at a half-integer
            // boundary (e.g. R=127.5 rounds to 128, then inverts to 127; inverting first gives 127.5, which
            // itself rounds to 128 — a different, wrong, answer).
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

    /// <summary>Loads <see cref="Lanes"/> contiguous bytes and widens them to float via hardware byte-&gt;ushort-&gt;uint-&gt;float widen/convert, not a scalar per-lane cast. Builds the zero-padded 16-byte vector directly from an 8-byte vector load plus a zero upper half — the load-side mirror of <see cref="StoreInterleavedRgb"/>'s <c>Vector128.Create(NarrowRounded(...), Vector64&lt;byte&gt;.Zero)</c> — rather than routing already-contiguous source bytes through a stack buffer just to satisfy <see cref="Vector128"/>'s 16-byte <see cref="Vector128.Create{T}(System.ReadOnlySpan{T})"/> overload.</summary>
    private static Vector256<float> LoadWidened(ReadOnlySpan<byte> source, int offset)
    {
        var byteVec = Vector128.Create(Vector64.LoadUnsafe(ref MemoryMarshal.GetReference(source.Slice(offset, Lanes))), Vector64<byte>.Zero);
        return WidenBytes(byteVec);
    }

    /// <summary>Gathers <see cref="Lanes"/> pixels' R/G/B bytes out of interleaved RGB (an unavoidable scalar strided read — see the type-level remarks) and widens each channel to float via the same hardware widen chain as <see cref="LoadWidened"/>.</summary>
    private static (Vector256<float> R, Vector256<float> G, Vector256<float> B) LoadWidenedInterleaved(ReadOnlySpan<byte> rgb, int i)
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

        return (WidenBytes(Vector128.LoadUnsafe(ref rPadded[0])), WidenBytes(Vector128.LoadUnsafe(ref gPadded[0])), WidenBytes(Vector128.LoadUnsafe(ref bPadded[0])));
    }

    /// <summary><paramref name="byteVec"/>'s first <see cref="Lanes"/> bytes (the rest is don't-care padding, never read past lane 7) widened to a <see cref="Vector256{Single}"/> — byte-&gt;ushort-&gt;uint widen, then a hardware uint-&gt;float convert, no scalar casts.</summary>
    private static Vector256<float> WidenBytes(Vector128<byte> byteVec)
    {
        var ushortVec = Vector128.WidenLower(byteVec);
        var (uintLower, uintUpper) = Vector128.Widen(ushortVec);
        return Vector256.ConvertToSingle(Vector256.Create(uintLower, uintUpper));
    }

    private static Vector256<int> ToRoundedInt(Vector256<float> value) => Vector256.ConvertToInt32(value + RoundingBias);

    /// <summary>Rounds <paramref name="value"/>'s <see cref="Lanes"/> lanes down to a <see cref="Vector64{Byte}"/> (int-&gt;uint-&gt;ushort-&gt;byte narrow, no scalar casts) — the same chain <see cref="StoreRounded"/>'s planar fast path uses.</summary>
    private static Vector64<byte> NarrowRounded(Vector256<float> value)
    {
        var asUInt = ToRoundedInt(value).AsUInt32();
        var narrowedToUShort = Vector128.Narrow(asUInt.GetLower(), asUInt.GetUpper());
        return Vector128.Narrow(narrowedToUShort, Vector128<ushort>.Zero).GetLower();
    }

    /// <summary>
    /// Rounds three per-channel <see cref="Lanes"/>-wide vectors and writes them directly as
    /// <see cref="Lanes"/> * 3 interleaved r,g,b,r,g,b,... bytes — the common decode-to-RGB24 output shape.
    /// Builds the 24-byte run via byte shuffles instead of <see cref="StoreRounded"/>'s scalar per-lane,
    /// stride-3 store loop: measured (dotnet-trace CPU profile) as ~36% of total decode self-time before
    /// this change, split roughly evenly between this method's old scalar store and <see cref="YCbCrToRgb"/>
    /// itself — by far the largest single hotspot bucket, ahead of the IDCT and entropy decode.
    /// </summary>
    private static void StoreInterleavedRgb(Vector256<float> rv, Vector256<float> gv, Vector256<float> bv, Span<byte> destination, int firstOffset)
    {
        var r = Vector128.Create(NarrowRounded(rv), Vector64<byte>.Zero);
        var g = Vector128.Create(NarrowRounded(gv), Vector64<byte>.Zero);
        var b = Vector128.Create(NarrowRounded(bv), Vector64<byte>.Zero);

        var lo = Vector128.Shuffle(r, RgbShuffleR0) | Vector128.Shuffle(g, RgbShuffleG0) | Vector128.Shuffle(b, RgbShuffleB0);
        var hi = Vector128.Shuffle(r, RgbShuffleR1) | Vector128.Shuffle(g, RgbShuffleG1) | Vector128.Shuffle(b, RgbShuffleB1);

        lo.StoreUnsafe(ref destination[firstOffset]);
        hi.GetLower().StoreUnsafe(ref destination[firstOffset + 16]);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> and writes the <see cref="Lanes"/> resulting bytes into
    /// <paramref name="destination"/> at the given <paramref name="stride"/>.
    /// </summary>
    /// <remarks>
    /// Two genuinely different shapes, not one path with a fast-path check bolted on top: the
    /// <c>stride == 1</c>/no-invert case (the planar case — <see cref="RgbToYCbCr"/>'s <c>y</c>/<c>cb</c>/<c>cr</c>
    /// outputs) narrows straight to a <see cref="Vector64{Byte}"/> (int-&gt;uint-&gt;ushort-&gt;byte) and does
    /// a genuine vector store, no loop at all. The interleaved (RGB/CMYK scatter) and invert (YCCK) cases keep
    /// the plain <c>stackalloc int[]</c> + scalar-cast loop — <b>measured</b> (BenchmarkDotNet, not just
    /// reasoned about) that routing them through the same narrow chain as the planar case made decode slower,
    /// not faster: the narrow chain's extra instructions aren't free, and when the store was going to stay a
    /// scalar per-lane loop regardless (unavoidable for a strided destination), paying for the narrow up front
    /// bought nothing. Isolated by 4:4:4 decode specifically (it never calls the chroma upsampler, so it's the
    /// one benchmark scenario that exercises only this path) regressing ~5% when both cases shared the narrow
    /// chain — reverted for this case rather than kept on the strength of it "should" be faster.
    /// </remarks>
    private static void StoreRounded(Vector256<float> value, Span<byte> destination, int firstOffset, int stride, bool invert = false)
    {
        if (stride == 1 && !invert)
        {
            NarrowRounded(value).StoreUnsafe(ref destination[firstOffset]);
            return;
        }

        Span<int> rounded = stackalloc int[Lanes];
        ToRoundedInt(value).StoreUnsafe(ref rounded[0]);
        for (int lane = 0; lane < Lanes; lane++)
        {
            destination[firstOffset + (lane * stride)] = (byte)(invert ? 255 - rounded[lane] : rounded[lane]);
        }
    }

    private static Vector256<float> Clamp(Vector256<float> value) => Vector256.Min(Vector256.Max(value, VZero), V255);
}
