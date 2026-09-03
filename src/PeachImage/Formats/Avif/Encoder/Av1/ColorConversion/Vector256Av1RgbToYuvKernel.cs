using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

/// <summary>
/// SIMD RGB24-&gt;YUV kernel using <see cref="Vector256{T}"/>'s cross-platform generic static API: the
/// BT.601 multiply-add chain (see <see cref="Av1RgbToYuvCoefficients"/>) is computed 8 pixels at a time.
/// Selected only when <see cref="Vector256.IsHardwareAccelerated"/> (effectively: AVX/AVX2 present),
/// mirroring <see cref="Jpeg.ColorConversion.Vector256ColorConverter"/>'s widen/store approach.
/// </summary>
internal sealed class Vector256Av1RgbToYuvKernel : IAv1RgbToYuvKernel
{
    private const int Lanes = 8;

    private static readonly Vector256<float> VKr = Vector256.Create(Av1RgbToYuvCoefficients.Kr);
    private static readonly Vector256<float> VKg = Vector256.Create(Av1RgbToYuvCoefficients.Kg);
    private static readonly Vector256<float> VKb = Vector256.Create(Av1RgbToYuvCoefficients.Kb);
    private static readonly Vector256<float> VKrInvHalf = Vector256.Create(Av1RgbToYuvCoefficients.KrInvHalf);
    private static readonly Vector256<float> VKbInvHalf = Vector256.Create(Av1RgbToYuvCoefficients.KbInvHalf);
    private static readonly Vector256<float> V128f = Vector256.Create(128f);
    private static readonly Vector256<float> VZero = Vector256<float>.Zero;
    private static readonly Vector256<float> V255 = Vector256.Create(255f);
    private static readonly Vector256<float> RoundingBias = Vector256.Create(0.5f);

    public void ConvertMonoChrome(ReadOnlySpan<byte> gray, Span<int> yOut, int pixelCount)
    {
        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var widened = WidenBytesToInt(gray, i);
            widened.StoreUnsafe(ref yOut[i]);
        }

        for (; i < pixelCount; i++)
        {
            yOut[i] = gray[i];
        }
    }

    public void RgbToYuvFullRes(ReadOnlySpan<byte> rgb, Span<int> yOut, Span<float> cbFullOut, Span<float> crFullOut, int pixelCount)
    {
        int i = 0;
        for (; i + Lanes <= pixelCount; i += Lanes)
        {
            var (rv, gv, bv) = LoadWidenedInterleaved(rgb, i);

            var yv = (VKr * rv) + (VKg * gv) + (VKb * bv);
            var cbv = V128f + ((bv - yv) * VKbInvHalf);
            var crv = V128f + ((rv - yv) * VKrInvHalf);

            StoreRoundedY(yv, yOut, i);
            cbv.StoreUnsafe(ref cbFullOut[i]);
            crv.StoreUnsafe(ref crFullOut[i]);
        }

        for (; i < pixelCount; i++)
        {
            ScalarAv1RgbToYuvKernel.ConvertPixel(rgb, i, yOut, cbFullOut, crFullOut);
        }
    }

    /// <summary>Loads <see cref="Lanes"/> contiguous bytes and widens them to a <see cref="Vector256{Int32}"/> via hardware byte-&gt;ushort-&gt;uint widen, reinterpreted as int (every value is 0-255, so the uint/int bit pattern is identical).</summary>
    private static Vector256<int> WidenBytesToInt(ReadOnlySpan<byte> source, int offset)
    {
        var byteVec = Vector128.Create(Vector64.LoadUnsafe(ref MemoryMarshal.GetReference(source.Slice(offset, Lanes))), Vector64<byte>.Zero);
        var ushortVec = Vector128.WidenLower(byteVec);
        var (uintLower, uintUpper) = Vector128.Widen(ushortVec);
        return Vector256.Create(uintLower, uintUpper).AsInt32();
    }

    /// <summary>Gathers <see cref="Lanes"/> pixels' R/G/B bytes out of interleaved RGB (an unavoidable scalar strided read -- the source layout is array-of-structs, not a contiguous run) and widens each channel to float via the same hardware widen chain as <see cref="Jpeg.ColorConversion.Vector256ColorConverter.LoadWidened"/>.</summary>
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

        return (WidenBytesToFloat(Vector128.LoadUnsafe(ref rPadded[0])), WidenBytesToFloat(Vector128.LoadUnsafe(ref gPadded[0])), WidenBytesToFloat(Vector128.LoadUnsafe(ref bPadded[0])));
    }

    /// <summary><paramref name="byteVec"/>'s first <see cref="Lanes"/> bytes (the rest is don't-care padding, never read past lane 7) widened to a <see cref="Vector256{Single}"/> -- byte-&gt;ushort-&gt;uint widen, then a hardware uint-&gt;float convert, no scalar casts.</summary>
    private static Vector256<float> WidenBytesToFloat(Vector128<byte> byteVec)
    {
        var ushortVec = Vector128.WidenLower(byteVec);
        var (uintLower, uintUpper) = Vector128.Widen(ushortVec);
        return Vector256.ConvertToSingle(Vector256.Create(uintLower, uintUpper));
    }

    /// <summary>Clamps to [0, 255] and rounds half-up (add 0.5, truncate) -- see <see cref="ScalarAv1RgbToYuvKernel.ClampToByte"/>'s remarks for why this bias-and-truncate trick is exact here and kept identical across tiers.</summary>
    private static void StoreRoundedY(Vector256<float> yv, Span<int> yOut, int offset)
    {
        var clamped = Vector256.Min(Vector256.Max(yv, VZero), V255);
        var rounded = Vector256.ConvertToInt32(clamped + RoundingBias);
        rounded.StoreUnsafe(ref yOut[offset]);
    }
}
