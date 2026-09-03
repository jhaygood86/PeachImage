using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// SIMD quantize kernel using <see cref="Vector128{T}"/>'s cross-platform generic static API: 2
/// coefficients at a time. JITs to SSE2 on x86 and AdvSimd on Arm from one source file. See
/// <see cref="Vector256Av1QuantizeKernel"/>'s remarks for the int32-&lt;-&gt;double widen/narrow chain and
/// the manual away-from-zero rounding this and the 256-bit kernel share.
/// </summary>
internal sealed class Vector128Av1QuantizeKernel : IAv1QuantizeKernel
{
    private const int Lanes = 2;

    private static readonly Vector128<double> Half = Vector128.Create(0.5);
    private static readonly Vector128<double> Zero = Vector128<double>.Zero;

    public void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, double dcReciprocal, double acReciprocal)
    {
        int total = size * size;
        var acRecipVec = Vector128.Create(acReciprocal);

        int i = 0;
        if (total >= Lanes)
        {
            // First Lanes-wide chunk: lane 0 is the DC coefficient (dcReciprocal), the rest are AC.
            Span<double> firstRecip = stackalloc double[Lanes];
            firstRecip[0] = dcReciprocal;
            for (int lane = 1; lane < Lanes; lane++)
            {
                firstRecip[lane] = acReciprocal;
            }

            QuantizeChunk(coeff, levelsOut, 0, Vector128.Create((ReadOnlySpan<double>)firstRecip));
            i = Lanes;
        }

        for (; i + Lanes <= total; i += Lanes)
        {
            QuantizeChunk(coeff, levelsOut, i, acRecipVec);
        }

        for (; i < total; i++)
        {
            double recip = i == 0 ? dcReciprocal : acReciprocal;
            levelsOut[i] = ScalarAv1QuantizeKernel.RoundAwayFromZero(coeff[i] * recip);
        }
    }

    private static void QuantizeChunk(ReadOnlySpan<int> coeff, Span<int> levelsOut, int offset, Vector128<double> reciprocal)
    {
        var coeffInt = Vector64.Create(coeff.Slice(offset, Lanes));
        var (lo, hi) = Vector64.Widen(coeffInt);
        var coeffDouble = Vector128.ConvertToDouble(Vector128.Create(lo, hi));

        var rounded = RoundAwayFromZero(coeffDouble * reciprocal);

        var roundedLong = Vector128.ConvertToInt64(rounded);
        // Narrow needs two same-width vectors; pad the upper half with zero and keep only the real lower lanes.
        var roundedInt = Vector128.Narrow(roundedLong, Vector128<long>.Zero).GetLower();
        roundedInt.StoreUnsafe(ref levelsOut[offset]);
    }

    /// <summary><c>sign(value) * floor(|value| + 0.5)</c> -- see <see cref="Vector256Av1QuantizeKernel.RoundAwayFromZero"/>'s remarks.</summary>
    private static Vector128<double> RoundAwayFromZero(Vector128<double> value)
    {
        var floor = Vector128.Floor(Vector128.Abs(value) + Half);
        var negative = Vector128.LessThan(value, Zero);
        return Vector128.ConditionalSelect(negative, -floor, floor);
    }
}
