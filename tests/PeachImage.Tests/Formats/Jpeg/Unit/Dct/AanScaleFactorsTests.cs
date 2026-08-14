using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Dct;

/// <summary>
/// Validates <see cref="AanScaleFactors.OneDimensional"/> against its closed-form definition directly —
/// independent of any DCT kernel, so it can't be fooled by a bug shared between a scale-factor table and
/// whatever kernel consumes it.
/// </summary>
public class AanScaleFactorsTests
{
    [Fact]
    public void OneDimensional_MatchesClosedFormDefinition()
    {
        var factors = AanScaleFactors.OneDimensional;
        Assert.Equal(8, factors.Length);

        for (int u = 0; u < 8; u++)
        {
            double expected = (u == 0 || u == 4) ? 1.0 : Math.Sqrt(2) * Math.Cos(u * Math.PI / 16.0);
            Assert.Equal(expected, factors[u], precision: 12);
        }
    }

    [Fact]
    public void BuildInverseDequantTable_ScalesEachEntryByOuterProductOfOneDimensional()
    {
        var dequant = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            dequant[i] = (ushort)(i + 1);
        }

        var scaled = AanScaleFactors.BuildInverseDequantTable(dequant);
        var s = AanScaleFactors.OneDimensional;

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                // BuildInverseDequantTable returns float[], so compare against the same float rounding
                // the production code applies rather than full double precision.
                float expected = (float)(dequant[(v * 8) + u] * s[u] * s[v]);
                Assert.Equal(expected, scaled[(v * 8) + u]);
            }
        }
    }

    [Fact]
    public void BuildForwardQuantTable_ScalesEachEntryByOuterProductOfOneDimensional()
    {
        var quant = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            quant[i] = (ushort)(i + 1);
        }

        var scaled = AanScaleFactors.BuildForwardQuantTable(quant);
        var s = AanScaleFactors.OneDimensional;

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double expected = quant[(v * 8) + u] * s[u] * s[v];
                Assert.Equal(expected, scaled[(v * 8) + u], precision: 12);
            }
        }
    }
}
