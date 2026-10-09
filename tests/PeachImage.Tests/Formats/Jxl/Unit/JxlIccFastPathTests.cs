using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.VarDct;
using PeachImage.Internal.Icc;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>The bulk matrix/TRC conversion of XYB output into an embedded ICC profile must agree with the general per-pixel ICC engine.</summary>
public class JxlIccFastPathTests
{
    [Theory]
    [InlineData("conformance_progressive.jxl")]
    [InlineData("icc_lossless.jxl")]
    public void RgbMatrixTrcFastPath_MatchesTheGeneralEngine(string asset)
    {
        byte[] icc = JxlCodestreamHeaders.Read(JxlContainer.Parse(JxlTestAssets.Load(asset)).Codestream.ToArray()).IccProfile
            ?? throw new InvalidOperationException("The asset has no ICC profile.");
        var profile = JxlIccOutput.OpenProfile(icc, gray: false);
        Assert.True(profile.TryGetFromXyzMatrixTrc(IccIntent.RelativeColorimetric, out _, out _), "The asset's profile is not a matrix/TRC profile.");

        const int side = 23;
        int count = side * side * side;
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = new float[count];
        }

        int n = 0;
        for (int r = 0; r < side; r++)
        {
            for (int g = 0; g < side; g++)
            {
                for (int b = 0; b < side; b++)
                {
                    planes[0][n] = r / (side - 1f);
                    planes[1][n] = g / (side - 1f);
                    planes[2][n] = b / (side - 1f);
                    n++;
                }
            }
        }

        var expected = new float[3][];
        var toXyz = IccColorMath.XyzD50ToLinearSrgb.Inverse();
        for (int c = 0; c < 3; c++)
        {
            expected[c] = new float[count];
        }

        Span<double> device = stackalloc double[3];
        for (int i = 0; i < count; i++)
        {
            profile.FromXyzD50(toXyz.Multiply(new IccVector3(planes[0][i], planes[1][i], planes[2][i])), IccIntent.RelativeColorimetric, device);
            for (int c = 0; c < 3; c++)
            {
                expected[c][i] = (float)Math.Clamp(device[c], 0.0, 1.0);
            }
        }

        JxlIccOutput.FromLinearSrgb(profile, planes, count, count, 1);

        double worst = 0;
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < count; i++)
            {
                worst = Math.Max(worst, Math.Abs(expected[c][i] - planes[c][i]));
            }
        }

        Assert.True(worst < 2e-4, $"The fast path deviates from the engine by {worst}.");
    }
}
