using System.Runtime.Intrinsics;

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

    public void YCbCrToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int pixelCount)
    {
        int i = 0;
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

    /// <summary>Loads <see cref="Lanes"/> contiguous bytes and widens them to float via hardware byte-&gt;ushort-&gt;uint-&gt;float widen/convert, not a scalar per-lane cast.</summary>
    private static Vector256<float> LoadWidened(ReadOnlySpan<byte> source, int offset)
    {
        Span<byte> padded = stackalloc byte[16];
        source.Slice(offset, Lanes).CopyTo(padded);
        return WidenBytes(padded);
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

        return (WidenBytes(rPadded), WidenBytes(gPadded), WidenBytes(bPadded));
    }

    /// <summary><paramref name="padded16"/>'s first <see cref="Lanes"/> bytes (the rest is don't-care padding, never read past lane 7) widened to a <see cref="Vector256{Single}"/> — byte-&gt;ushort-&gt;uint widen, then a hardware uint-&gt;float convert, no scalar casts.</summary>
    private static Vector256<float> WidenBytes(ReadOnlySpan<byte> padded16)
    {
        var byteVec = Vector128.Create(padded16);
        var ushortVec = Vector128.WidenLower(byteVec);
        var (uintLower, uintUpper) = Vector128.Widen(ushortVec);
        return Vector256.ConvertToSingle(Vector256.Create(uintLower, uintUpper));
    }

    private static Vector256<int> ToRoundedInt(Vector256<float> value) => Vector256.ConvertToInt32(value + RoundingBias);

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
            var asUInt = ToRoundedInt(value).AsUInt32();
            var narrowedToUShort = Vector128.Narrow(asUInt.GetLower(), asUInt.GetUpper());
            var bytes = Vector128.Narrow(narrowedToUShort, Vector128<ushort>.Zero).GetLower();
            bytes.CopyTo(destination.Slice(firstOffset, Lanes));
            return;
        }

        Span<int> rounded = stackalloc int[Lanes];
        ToRoundedInt(value).CopyTo(rounded);
        for (int lane = 0; lane < Lanes; lane++)
        {
            destination[firstOffset + (lane * stride)] = (byte)(invert ? 255 - rounded[lane] : rounded[lane]);
        }
    }

    private static Vector256<float> Clamp(Vector256<float> value) => Vector256.Min(Vector256.Max(value, VZero), V255);
}
