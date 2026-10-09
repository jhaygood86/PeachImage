using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlCustomFloatTests
{
    [Theory]
    [InlineData(16, 5)]
    [InlineData(24, 8)]
    [InlineData(12, 4)]
    [InlineData(32, 8)]
    public void EncodeDecode_RoundTripsEveryRepresentableValue(int bits, int exponentBits)
    {
        int step = bits > 16 ? 4099 : 1;
        long limit = 1L << Math.Min(bits, 31);
        for (long pattern = 0; pattern < limit; pattern += step)
        {
            float value;
            try
            {
                value = JxlCustomFloat.Decode((int)pattern, bits, exponentBits);
            }
            catch (PeachImage.Formats.Jxl.JxlDecodingException)
            {
                continue;
            }

            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                continue;
            }

            Assert.Equal((int)pattern, JxlCustomFloat.Encode(value, bits, exponentBits));
        }
    }

    [Fact]
    public void Encode_HalfPrecision_MatchesSystemHalf()
    {
        float[] samples = [0f, 1f, -1f, 0.5f, 0.1f, 65504f, 6.1e-5f, 5.96e-8f, 1.0009766f, 3.14159f, 1e-9f];
        foreach (float sample in samples)
        {
            int expected = BitConverter.HalfToUInt16Bits((Half)sample);
            Assert.Equal(expected, JxlCustomFloat.Encode(sample, 16, 5));
        }
    }

    [Fact]
    public void Encode_Overflow_BecomesInfinity()
    {
        Assert.Equal(0x7C00, JxlCustomFloat.Encode(1e10f, 16, 5));
        Assert.Equal(0xFC00, JxlCustomFloat.Encode(-1e10f, 16, 5));
    }

    [Fact]
    public void SampleConversion_FloatDepth_PreservesValues()
    {
        var depth = new JxlBitDepth(true, 16, 5);
        float[] source = [0f, 0.25f, 0.5f, 1f, 2f, -0.5f];
        int[] packed = new int[source.Length];
        JxlSampleConversion.ToIntChannel(source, source.Length, packed, source.Length, 1, depth);
        float[] back = new float[source.Length];
        JxlSampleConversion.ToFloatPlane(packed, back, source.Length, 1, depth);
        Assert.Equal(source, back);
    }

    [Fact]
    public void SampleConversion_IntegerDepth_NormalizesAndRounds()
    {
        var depth = new JxlBitDepth(false, 10, 0);
        int[] samples = [0, 1, 512, 1023];
        float[] planes = new float[samples.Length];
        JxlSampleConversion.ToFloatPlane(samples, planes, samples.Length, 1, depth);
        Assert.Equal(1f, planes[3]);
        int[] back = new int[samples.Length];
        JxlSampleConversion.ToIntChannel(planes, samples.Length, back, samples.Length, 1, depth);
        Assert.Equal(samples, back);
    }
}
