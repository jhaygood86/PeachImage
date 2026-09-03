using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

/// <summary>
/// SIMD RGB24-&gt;YUV kernel using <see cref="Vector128{T}"/>'s cross-platform generic static API: the
/// BT.601 multiply-add chain (see <see cref="Av1RgbToYuvCoefficients"/>) is computed 4 pixels at a time.
/// JITs to SSE2 on x86 and AdvSimd on Arm from one source file, mirroring
/// <see cref="Jpeg.ColorConversion.Vector128ColorConverter"/>'s widen/store approach.
/// </summary>
internal sealed class Vector128Av1RgbToYuvKernel : IAv1RgbToYuvKernel
{
    private const int Lanes = 4;

    private static readonly Vector128<float> VKr = Vector128.Create(Av1RgbToYuvCoefficients.Kr);
    private static readonly Vector128<float> VKg = Vector128.Create(Av1RgbToYuvCoefficients.Kg);
    private static readonly Vector128<float> VKb = Vector128.Create(Av1RgbToYuvCoefficients.Kb);
    private static readonly Vector128<float> VKrInvHalf = Vector128.Create(Av1RgbToYuvCoefficients.KrInvHalf);
    private static readonly Vector128<float> VKbInvHalf = Vector128.Create(Av1RgbToYuvCoefficients.KbInvHalf);
    private static readonly Vector128<float> V128f = Vector128.Create(128f);
    private static readonly Vector128<float> VZero = Vector128<float>.Zero;
    private static readonly Vector128<float> V255 = Vector128.Create(255f);
    private static readonly Vector128<float> RoundingBias = Vector128.Create(0.5f);

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

    private static Vector128<int> WidenBytesToInt(ReadOnlySpan<byte> source, int offset)
    {
        Span<byte> padded = stackalloc byte[16];
        source.Slice(offset, Lanes).CopyTo(padded);
        var byteVec = Vector128.LoadUnsafe(ref padded[0]);
        var ushortVec = Vector128.WidenLower(byteVec);
        return Vector128.WidenLower(ushortVec).AsInt32();
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

        return (WidenBytesToFloat(rPadded), WidenBytesToFloat(gPadded), WidenBytesToFloat(bPadded));
    }

    /// <summary><paramref name="padded16"/>'s first <see cref="Lanes"/> bytes widened to a <see cref="Vector128{Single}"/> via byte-&gt;ushort-&gt;uint widen then a hardware uint-&gt;float convert -- the rest of the 16 bytes is don't-care padding, never read past lane 3.</summary>
    private static Vector128<float> WidenBytesToFloat(ReadOnlySpan<byte> padded16)
    {
        var byteVec = Vector128.LoadUnsafe(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(padded16));
        var ushortVec = Vector128.WidenLower(byteVec);
        var uintVec = Vector128.WidenLower(ushortVec);
        return Vector128.ConvertToSingle(uintVec);
    }

    /// <summary>Clamps to [0, 255] and rounds half-up (add 0.5, truncate) -- see <see cref="ScalarAv1RgbToYuvKernel.ClampToByte"/>'s remarks for why this bias-and-truncate trick is exact here and kept identical across tiers.</summary>
    private static void StoreRoundedY(Vector128<float> yv, Span<int> yOut, int offset)
    {
        var clamped = Vector128.Min(Vector128.Max(yv, VZero), V255);
        var rounded = Vector128.ConvertToInt32(clamped + RoundingBias);
        rounded.StoreUnsafe(ref yOut[offset]);
    }
}
