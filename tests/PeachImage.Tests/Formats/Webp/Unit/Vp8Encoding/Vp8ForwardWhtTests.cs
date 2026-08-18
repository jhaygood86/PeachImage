using PeachImage.Formats.Webp.Decoding.Vp8.Dct;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// Validates <see cref="Vp8ForwardWht"/> by round-tripping through the real, unmodified
/// <see cref="Vp8ScalarInverseWht"/>. A 4x4 Walsh-Hadamard transform is self-inverse up to a factor of 16 (two
/// separable 4-point transforms per axis, each scaling by 4); the forward transform's final &gt;&gt;1 and the
/// inverse's final &gt;&gt;3 together divide by exactly that factor, so forward-then-inverse should reproduce the
/// original 16 values with only small integer-truncation error -- tighter than <see cref="Vp8ForwardDct"/>'s,
/// since the WHT is pure add/subtract with no lossy rotation constants.
/// </summary>
public class Vp8ForwardWhtTests
{
    [Fact]
    public void Transform_AllZero_RoundTripsExactly()
    {
        Span<short> dc = stackalloc short[16];
        Span<short> y2 = stackalloc short[16];
        Span<short> reconstructed = stackalloc short[16];

        Vp8ForwardWht.Transform(dc, y2);
        Vp8ScalarInverseWht.Transform(y2, reconstructed);

        foreach (short v in reconstructed)
        {
            Assert.Equal(0, v);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Transform_RandomDcValues_RoundTripsWithinBoundedError(int seed)
    {
        var random = new Random(seed);
        Span<short> dc = stackalloc short[16];
        for (int i = 0; i < 16; i++)
        {
            dc[i] = (short)random.Next(-2048, 2048);
        }

        Span<short> y2 = stackalloc short[16];
        Vp8ForwardWht.Transform(dc, y2);

        Span<short> reconstructed = stackalloc short[16];
        Vp8ScalarInverseWht.Transform(y2, reconstructed);

        for (int i = 0; i < 16; i++)
        {
            Assert.True(Math.Abs(dc[i] - reconstructed[i]) <= 2, $"Index {i}: expected {dc[i]}, got {reconstructed[i]}.");
        }
    }

    [Fact]
    public void Transform_SingleNonZeroDc_RoundTripsWithinBoundedError()
    {
        Span<short> dc = stackalloc short[16];
        dc[5] = 1000;

        Span<short> y2 = stackalloc short[16];
        Vp8ForwardWht.Transform(dc, y2);

        Span<short> reconstructed = stackalloc short[16];
        Vp8ScalarInverseWht.Transform(y2, reconstructed);

        for (int i = 0; i < 16; i++)
        {
            Assert.True(Math.Abs(dc[i] - reconstructed[i]) <= 2, $"Index {i}: expected {dc[i]}, got {reconstructed[i]}.");
        }
    }
}
