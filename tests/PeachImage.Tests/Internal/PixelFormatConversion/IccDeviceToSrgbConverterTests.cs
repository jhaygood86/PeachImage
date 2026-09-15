using PeachImage.Internal.Icc;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Tests.Internal.PixelFormatConversion;

/// <summary>
/// Verifies <see cref="IccDeviceToSrgbConverter.XyzBatchToRgba"/>'s vectorized matrix/gamma tail agrees with a
/// plain scalar reference computed independently in this test (not by calling a separate "scalar mode" of the
/// production code — there isn't one; <see cref="IccDeviceToSrgbConverter.XyzBatchToRgba"/> picks its own tier
/// at runtime based on hardware support), mirroring this codebase's established Scalar-vs-SIMD parity pattern
/// (see <c>Formats.Jpeg.Unit.ColorConversion.ColorConversionAccuracyTests</c>).
/// </summary>
public class IccDeviceToSrgbConverterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(37)] // Deliberately not a multiple of 4, to exercise the tail loop past any SIMD batch.
    [InlineData(64)]
    public void XyzBatchToRgba_MatchesScalarReference(int pixelCount)
    {
        var random = new Random(1);
        var xs = new double[pixelCount];
        var ys = new double[pixelCount];
        var zs = new double[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            // A plausible XYZ range for real (in- and slightly out-of-gamut) device PCS values.
            xs[i] = random.NextDouble() * 1.1;
            ys[i] = random.NextDouble() * 1.1;
            zs[i] = random.NextDouble() * 1.1;
        }

        var actual = new byte[pixelCount * 4];
        IccDeviceToSrgbConverter.XyzBatchToRgba(xs, ys, zs, actual);

        var expected = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            var linear = IccColorMath.XyzD50ToLinearSrgb.Multiply(new IccVector3(xs[i], ys[i], zs[i]));
            int offset = i * 4;
            expected[offset] = IccColorMath.LinearToSrgbByte(linear.X);
            expected[offset + 1] = IccColorMath.LinearToSrgbByte(linear.Y);
            expected[offset + 2] = IccColorMath.LinearToSrgbByte(linear.Z);
            expected[offset + 3] = 255;
        }

        Assert.Equal(expected, actual);
    }
}
