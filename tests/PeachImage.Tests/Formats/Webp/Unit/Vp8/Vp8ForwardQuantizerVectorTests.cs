using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>The vector quantizer/dequantizer must agree exactly with the scalar definitions, including level values above 255 and extreme coefficients.</summary>
public class Vp8ForwardQuantizerVectorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Quantize_MatchesScalar(int seed)
    {
        var rng = new Random(seed);
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            var coefficients = new short[16];
            int style = rng.Next(4);
            for (int i = 0; i < 16; i++)
            {
                coefficients[i] = style switch
                {
                    0 => (short)rng.Next(short.MinValue, short.MaxValue + 1),
                    1 => (short)rng.Next(-300, 300),
                    2 => rng.Next(3) == 0 ? (short)rng.Next(-4000, 4000) : (short)0,
                    _ => (short)(rng.Next(2) == 0 ? short.MinValue : short.MaxValue),
                };
            }

            // Real quantizer steps are 4..157 (dc) and up to ~284 for the Y2 AC step; cover tiny steps too, where levels exceed 255.
            int dc = rng.Next(1, 300);
            int ac = rng.Next(1, 300);

            var expected = new short[16];
            var actual = new short[16];
            int expectedLast = Vp8ForwardQuantizer.QuantizeScalar(coefficients, dc, ac, expected);
            int actualLast = Vp8ForwardQuantizer.QuantizeVector(coefficients, dc, ac, actual);

            Assert.Equal(expected, actual);
            Assert.Equal(expectedLast, actualLast);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Quantize_ExactMultiplesOfTheStep_MatchScalar(int seed)
    {
        // Numerators of exactly q * k - 1, q * k and q * k + q/2 are where a sloppy division goes wrong.
        var rng = new Random(seed);
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            int q = rng.Next(1, 300);
            var coefficients = new short[16];
            for (int i = 0; i < 16; i++)
            {
                int k = rng.Next(0, 32767 / q);
                int offset = rng.Next(-1, 3) - (q / 2);
                coefficients[i] = (short)Math.Clamp((k * q) + offset, short.MinValue, short.MaxValue);
                if (rng.Next(2) == 0)
                {
                    coefficients[i] = (short)-coefficients[i];
                }
            }

            var expected = new short[16];
            var actual = new short[16];
            Assert.Equal(Vp8ForwardQuantizer.QuantizeScalar(coefficients, q, q, expected), Vp8ForwardQuantizer.QuantizeVector(coefficients, q, q, actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Dequantize_MatchesScalar()
    {
        var rng = new Random(11);
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            var levels = new short[16];
            for (int i = 0; i < 16; i++)
            {
                levels[i] = rng.Next(3) == 0 ? (short)rng.Next(-2048, 2048) : (short)0;
            }

            int dc = rng.Next(1, 300);
            int ac = rng.Next(1, 300);
            var expected = new short[16];
            var actual = new short[16];
            Array.Fill(actual, (short)0x7777); // Must be fully overwritten, not merely cleared first.
            Vp8ForwardQuantizer.DequantizeScalar(levels, dc, ac, expected);
            Vp8ForwardQuantizer.DequantizeVector(levels, dc, ac, actual);
            Assert.Equal(expected, actual);
        }
    }
}
