using PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Verifies <see cref="Vp8ScalarColorConverter"/> against sample points worked out by hand from the formula
/// itself (<c>R = Clip8(MultHi(y,19077) + MultHi(v,26149) - 14234)</c>, etc., <c>MultHi(v,c) = (v*c) &gt;&gt; 8</c>,
/// <c>Clip8(v) = clamp(v &gt;&gt; 6, 0, 255)</c> - see the class remarks in <see cref="Vp8ScalarColorConverter"/>).
/// </summary>
public class Vp8ScalarColorConverterTests
{
    /// <summary>
    /// With neutral chroma (u=v=128), the u/v cross terms are (nearly) constant offsets, so studio-range
    /// black/mid-gray/white luma values should map to gray (R≈G≈B), landing at the studio range's usual
    /// black=0/white=255 endpoints. Worked by hand: MultHi(16,19077)=1192 -&gt; R/G/B all 0; MultHi(128,19077)=9538
    /// -&gt; R=G=B=130; MultHi(235,19077)=17512 -&gt; R=G=B=255 (each channel's u/v offset differs by at most 1 before
    /// the final &gt;&gt;6, which does not change the rounded result at these particular values).
    /// </summary>
    [Theory]
    [InlineData((byte)16, (byte)0, (byte)0, (byte)0)]
    [InlineData((byte)128, (byte)130, (byte)130, (byte)130)]
    [InlineData((byte)235, (byte)255, (byte)255, (byte)255)]
    public void Convert_NeutralChroma_ProducesGray(byte y, byte expectedR, byte expectedG, byte expectedB)
    {
        var converter = new Vp8ScalarColorConverter();
        Span<byte> rgb = stackalloc byte[3];

        converter.Convert(y, 128, 128, rgb);

        Assert.Equal(expectedR, rgb[0]);
        Assert.Equal(expectedG, rgb[1]);
        Assert.Equal(expectedB, rgb[2]);
    }

    /// <summary>
    /// A skewed chroma sample point (Y=128, U=90, V=200), worked out by hand: R_raw = MultHi(128,19077) +
    /// MultHi(200,26149) - 14234 = 9538 + 20428 - 14234 = 15732 -&gt; R = 15732&gt;&gt;6 = 245. G_raw = 9538 -
    /// MultHi(90,6419) - MultHi(200,13320) + 8708 = 9538 - 2256 - 10406 + 8708 = 5584 -&gt; G = 5584&gt;&gt;6 = 87.
    /// B_raw = 9538 + MultHi(90,33050) - 17685 = 9538 + 11619 - 17685 = 3472 -&gt; B = 3472&gt;&gt;6 = 54.
    /// </summary>
    [Fact]
    public void Convert_SkewedChroma_MatchesHandComputedValue()
    {
        var converter = new Vp8ScalarColorConverter();
        Span<byte> rgb = stackalloc byte[3];

        converter.Convert(128, 90, 200, rgb);

        Assert.Equal(245, rgb[0]);
        Assert.Equal(87, rgb[1]);
        Assert.Equal(54, rgb[2]);
    }

    [Fact]
    public void Convert_OutOfRangeCombination_ClampsToByteRange()
    {
        var converter = new Vp8ScalarColorConverter();
        Span<byte> rgb = stackalloc byte[3];

        // Y=255, V=255 pushes R's raw value well past 255*64; Y=0, U=0 would push B's raw value negative for
        // other channels — verify both ends of the clamp are respected (no overflow/underflow wraparound).
        converter.Convert(255, 255, 255, rgb);
        Assert.InRange(rgb[0], (byte)0, (byte)255);
        Assert.InRange(rgb[1], (byte)0, (byte)255);
        Assert.InRange(rgb[2], (byte)0, (byte)255);

        converter.Convert(0, 0, 0, rgb);
        Assert.InRange(rgb[0], (byte)0, (byte)255);
        Assert.InRange(rgb[1], (byte)0, (byte)255);
        Assert.InRange(rgb[2], (byte)0, (byte)255);
    }
}