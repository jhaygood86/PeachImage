using PeachImage.Formats.Webp.Decoding.Vp8.Dct;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Independently verifies <see cref="Vp8ScalarInverseDct"/> before it is wired into the full decode pipeline:
/// the DC-only fast path against a hand-derived algebraic simplification of the full butterfly, and the general
/// (has-AC-coefficients) path against a floating-point reference implementation of the same butterfly structure
/// that uses the true irrational trigonometric constants (rather than the fixed-point 20091/65536 and
/// 35468/65536 approximations <see cref="Vp8InverseTransformConstants"/> uses) - so a structural bug (wrong
/// index arithmetic, a swapped sign, a wrong output scatter position) would disagree with the float reference
/// too, while only the fixed-point quantization itself is allowed to differ (accommodated with a small tolerance).
/// </summary>
public class Vp8ScalarInverseDctTests
{
    /// <summary>
    /// Algebraic derivation: when only coefficients[0]=D is nonzero, every Mul1/Mul2 term in the butterfly
    /// receives a zero input (Mul1(0)=Mul2(0)=0), so both passes degenerate to propagating D unchanged into
    /// every one of the 16 output slots, each finally computed as (D+4)&gt;&gt;3 - exactly the dedicated fast path.
    /// </summary>
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)8)]
    [InlineData((short)-8)]
    [InlineData((short)100)]
    [InlineData((short)-100)]
    [InlineData((short)2040)]
    public void TransformAndAdd_DcOnly_AddsRoundedDcToAllSixteenPixels(short dc)
    {
        short[] coefficients = new short[16];
        coefficients[0] = dc;
        byte[] dst = new byte[16];
        Array.Fill(dst, (byte)100);

        Vp8ScalarInverseDct.TransformAndAdd(coefficients, dst, 0, 4);

        byte expected = (byte)Math.Clamp(100 + ((dc + 4) >> 3), 0, 255);
        foreach (byte actual in dst)
        {
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [MemberData(nameof(AcCoefficientCases))]
    public void TransformAndAdd_WithAcCoefficients_MatchesFloatingPointReference(short[] coefficients)
    {
        byte[] dst = new byte[16];
        Array.Fill(dst, (byte)128);

        Vp8ScalarInverseDct.TransformAndAdd(coefficients, dst, 0, 4);

        double[] reference = ReferenceIdct(coefficients);

        for (int i = 0; i < 16; i++)
        {
            double expected = Math.Clamp(128 + reference[i], 0, 255);
            Assert.True(Math.Abs(dst[i] - expected) <= 2.0, $"pixel {i}: expected ~{expected:F2}, got {dst[i]}.");
        }
    }

    public static TheoryData<short[]> AcCoefficientCases()
    {
        var data = new TheoryData<short[]>();
        data.Add(new short[] { 10, -5, 3, 0, 7, 0, -2, 4, 0, 6, 0, -3, 2, 0, 5, -1 });
        data.Add(new short[] { 100, -50, 30, 0, 70, 0, -20, 40, 0, 60, 0, -30, 20, 0, 50, -10 });
        data.Add(new short[] { -64, 12, -8, 5, 0, -9, 3, 7, -2, 0, 6, -4, 1, 3, 0, -5 });
        data.Add(new short[] { 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -200 });
        return data;
    }

    /// <summary>Same butterfly shape as <see cref="Vp8ScalarInverseDct"/>, but in floating point with true trig constants; returns the raw residual (no baseline added, no clamping, no integer rounding bias).</summary>
    private static double[] ReferenceIdct(short[] coefficients)
    {
        double c1 = (Math.Sqrt(2) * Math.Cos(Math.PI / 8)) - 1.0;
        double c2 = Math.Sqrt(2) * Math.Sin(Math.PI / 8);

        double Mul1(double v) => v + (v * c1);
        double Mul2(double v) => v * c2;

        var tmp = new double[16];
        for (int i = 0; i < 4; i++)
        {
            double in0 = coefficients[i];
            double in4 = coefficients[4 + i];
            double in8 = coefficients[8 + i];
            double in12 = coefficients[12 + i];

            double a = in0 + in8;
            double b = in0 - in8;
            double c = Mul2(in4) - Mul1(in12);
            double d = Mul1(in4) + Mul2(in12);

            tmp[(i * 4) + 0] = a + d;
            tmp[(i * 4) + 1] = b + c;
            tmp[(i * 4) + 2] = b - c;
            tmp[(i * 4) + 3] = a - d;
        }

        var result = new double[16];
        for (int i = 0; i < 4; i++)
        {
            double t0 = tmp[(0 * 4) + i];
            double t4 = tmp[(1 * 4) + i];
            double t8 = tmp[(2 * 4) + i];
            double t12 = tmp[(3 * 4) + i];

            double a = t0 + t8;
            double b = t0 - t8;
            double c = Mul2(t4) - Mul1(t12);
            double d = Mul1(t4) + Mul2(t12);

            result[(i * 4) + 0] = (a + d) / 8.0;
            result[(i * 4) + 1] = (b + c) / 8.0;
            result[(i * 4) + 2] = (b - c) / 8.0;
            result[(i * 4) + 3] = (a - d) / 8.0;
        }

        return result;
    }
}