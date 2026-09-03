using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Encoder.Av1.Quantization;

/// <summary>
/// SIMD quantize kernel using <see cref="Vector256{T}"/>'s cross-platform generic static API: 4
/// coefficients at a time. <see cref="Av1ForwardQuantizer.Quantize"/> only ever calls this with
/// <c>size</c> in {4, 8, 16, 32} so <c>size * size</c> is always a multiple of <see cref="Lanes"/>
/// (16, 64, 256, or 1024) -- the scalar tail below never actually executes for this class's real callers,
/// kept anyway for general correctness.
/// </summary>
/// <remarks>
/// There is no direct cross-platform int32-&lt;-&gt;double convert in <see cref="System.Runtime.Intrinsics"/>,
/// so each chunk widens int32 -&gt; int64 (sign-extending, exact for any int32 value) -&gt; double (exact:
/// every int32 value is exactly representable as a double), and narrows back double -&gt; int64 -&gt; int32
/// the same way. <see cref="MidpointRounding.AwayFromZero"/> is reproduced manually as
/// <c>sign(x) * floor(|x| + 0.5)</c> (<see cref="RoundAwayFromZero"/>) since the generic vector API only
/// exposes <c>Vector256.Floor</c>/<c>Ceiling</c>, not a rounding-mode-aware Round.
/// </remarks>
internal sealed class Vector256Av1QuantizeKernel : IAv1QuantizeKernel
{
    private const int Lanes = 4;

    private static readonly Vector256<double> Half = Vector256.Create(0.5);
    private static readonly Vector256<double> Zero = Vector256<double>.Zero;

    public void Quantize(ReadOnlySpan<int> coeff, Span<int> levelsOut, int size, double dcReciprocal, double acReciprocal)
    {
        int total = size * size;
        var acRecipVec = Vector256.Create(acReciprocal);

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

            QuantizeChunk(coeff, levelsOut, 0, Vector256.Create((ReadOnlySpan<double>)firstRecip));
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

    private static void QuantizeChunk(ReadOnlySpan<int> coeff, Span<int> levelsOut, int offset, Vector256<double> reciprocal)
    {
        var coeffInt = Vector128.Create(coeff.Slice(offset, Lanes));
        var (lo, hi) = Vector128.Widen(coeffInt);
        var coeffDouble = Vector256.ConvertToDouble(Vector256.Create(lo, hi));

        var rounded = RoundAwayFromZero(coeffDouble * reciprocal);

        var roundedLong = Vector256.ConvertToInt64(rounded);
        var roundedInt = Vector128.Narrow(roundedLong.GetLower(), roundedLong.GetUpper());
        roundedInt.StoreUnsafe(ref levelsOut[offset]);
    }

    /// <summary><c>sign(value) * floor(|value| + 0.5)</c> -- see the type-level remarks.</summary>
    private static Vector256<double> RoundAwayFromZero(Vector256<double> value)
    {
        var floor = Vector256.Floor(Vector256.Abs(value) + Half);
        var negative = Vector256.LessThan(value, Zero);
        return Vector256.ConditionalSelect(negative, -floor, floor);
    }
}
