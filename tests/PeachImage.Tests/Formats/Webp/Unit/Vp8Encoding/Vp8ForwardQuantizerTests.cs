using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

public class Vp8ForwardQuantizerTests
{
    [Fact]
    public void Quantize_AllZeroBlock_ReturnsLastZero()
    {
        Span<short> coefficients = stackalloc short[16];
        Span<short> quantized = stackalloc short[16];

        int last = Vp8ForwardQuantizer.Quantize(coefficients, 4, 4, quantized);

        Assert.Equal(0, last);
        foreach (short q in quantized)
        {
            Assert.Equal(0, q);
        }
    }

    [Fact]
    public void Quantize_OnlyDcNonZero_ReturnsLastOne()
    {
        Span<short> coefficients = stackalloc short[16];
        coefficients[0] = 40;
        Span<short> quantized = stackalloc short[16];

        int last = Vp8ForwardQuantizer.Quantize(coefficients, 8, 4, quantized);

        Assert.Equal(1, last);
        Assert.Equal(5, quantized[0]); // 40 / 8 = 5 exactly.
        for (int i = 1; i < 16; i++)
        {
            Assert.Equal(0, quantized[i]);
        }
    }

    /// <summary>Natural position 15 is scan position 15 (Vp8ZigZag.Order[15] == 15), so a coefficient there is the last possible nonzero and last must be 16.</summary>
    [Fact]
    public void Quantize_LastNaturalPositionNonZero_ReturnsLastSixteen()
    {
        Span<short> coefficients = stackalloc short[16];
        coefficients[15] = 20;
        Span<short> quantized = stackalloc short[16];

        int last = Vp8ForwardQuantizer.Quantize(coefficients, 4, 4, quantized);

        Assert.Equal(16, last);
        Assert.Equal(5, quantized[15]);
    }

    [Theory]
    [InlineData(37, 8)]
    [InlineData(-37, 8)]
    [InlineData(4, 8)]
    [InlineData(-4, 8)]
    [InlineData(3, 8)]
    [InlineData(0, 8)]
    public void QuantizeOne_RoundsToNearestWithSign(int coeff, int quant)
    {
        Span<short> coefficients = stackalloc short[16];
        coefficients[0] = (short)coeff;
        Span<short> quantized = stackalloc short[16];

        Vp8ForwardQuantizer.Quantize(coefficients, quant, quant, quantized);

        int expectedMagnitude = (Math.Abs(coeff) + (quant / 2)) / quant;
        int expected = coeff < 0 ? -expectedMagnitude : expectedMagnitude;
        Assert.Equal(expected, quantized[0]);
    }

    [Fact]
    public void Dequantize_RoundTripsQuantizedLevelsBackToNaturalOrder()
    {
        Span<short> coefficients = stackalloc short[16];
        coefficients[0] = 40; // DC
        coefficients[5] = -16; // some AC position
        Span<short> quantized = stackalloc short[16];
        Vp8ForwardQuantizer.Quantize(coefficients, 8, 4, quantized);

        Span<short> dequantized = stackalloc short[16];
        Vp8ForwardQuantizer.Dequantize(quantized, 8, 4, dequantized);

        Assert.Equal(40, dequantized[0]);
        Assert.Equal(-16, dequantized[5]);
        for (int i = 1; i < 16; i++)
        {
            if (i != 5)
            {
                Assert.Equal(0, dequantized[i]);
            }
        }
    }

    [Fact]
    public void Dequantize_PreviouslyPopulatedOutput_ClearsStaleEntries()
    {
        Span<short> quantized = stackalloc short[16];
        Span<short> output = stackalloc short[16];
        output.Fill(123);

        Vp8ForwardQuantizer.Dequantize(quantized, 4, 4, output);

        foreach (short v in output)
        {
            Assert.Equal(0, v);
        }
    }
}
