using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>log2_test.cc</c> (<c>Log2Test.GetMsb</c>/<c>Log2Test.AomCeilLog2</c>): an
/// exhaustive (not randomized -- libaom's own real test isn't either) check of
/// <see cref="Av1CdfAdaptation.FloorLog2"/> (libaom's own real <c>get_msb</c>, <c>aom_ports/bitops.h</c>)
/// against <see cref="Math.Floor(double)"/>/<see cref="Math.Log2(double)"/>, and
/// <see cref="Av1TileDecoder.CeilLog2"/> (libaom's own real <c>aom_ceil_log2</c>) against
/// <see cref="Math.Ceiling(double)"/>/<see cref="Math.Log2(double)"/> -- both entry points genuinely
/// shared, by name, across this project's own entropy coder, palette context, and CDEF code (see each
/// type's own remarks), so covering them here covers every one of those real call sites at once.
///
/// <para>Several other files in this project (<c>Av1Cdef</c>, <c>Av1BitReader</c>, <c>Av1BitWriter</c>) carry
/// their own private, byte-for-byte identical copies of the same shift-and-count loop rather than calling
/// <see cref="Av1CdfAdaptation.FloorLog2"/> directly -- real, duplicated logic, not merely untested code
/// covered by this port. Flagged separately as a follow-up rather than addressed here, since de-duplicating
/// them is a refactor, not a test-porting task.</para>
/// </summary>
public class Av1Log2Tests
{
    [Fact]
    public void FloorLog2_MatchesLibaomReference_ForSmallNumbers()
    {
        // libaom's own real test range: n from 1 to 9999 inclusive.
        for (uint n = 1; n < 10000; n++)
        {
            int expected = (int)Math.Floor(Math.Log2(n));
            Assert.Equal(expected, Av1CdfAdaptation.FloorLog2(n));
        }
    }

    [Fact]
    public void FloorLog2_MatchesLibaomReference_AtPowerOfTwoBoundaries()
    {
        for (int exponent = 2; exponent < 32; exponent++)
        {
            uint powerOfTwo = 1u << exponent;
            Assert.Equal(exponent - 1, Av1CdfAdaptation.FloorLog2(powerOfTwo - 1));
            Assert.Equal(exponent, Av1CdfAdaptation.FloorLog2(powerOfTwo));
            Assert.Equal(exponent, Av1CdfAdaptation.FloorLog2(powerOfTwo + 1));
        }
    }

    [Fact]
    public void CeilLog2_MatchesLibaomReference_ForSmallNumbers()
    {
        // libaom's own real aom_ceil_log2(0) special case: 0, not undefined (unlike get_msb(0)).
        Assert.Equal(0, Av1TileDecoder.CeilLog2(0));

        for (int n = 1; n < 10000; n++)
        {
            int expected = (int)Math.Ceiling(Math.Log2(n));
            Assert.Equal(expected, Av1TileDecoder.CeilLog2(n));
        }
    }

    [Fact]
    public void CeilLog2_MatchesLibaomReference_AtPowerOfTwoBoundaries()
    {
        for (int exponent = 2; exponent < 31; exponent++)
        {
            int powerOfTwo = 1 << exponent;
            Assert.Equal(exponent, Av1TileDecoder.CeilLog2(powerOfTwo - 1));
            Assert.Equal(exponent, Av1TileDecoder.CeilLog2(powerOfTwo));
            Assert.Equal(exponent + 1, Av1TileDecoder.CeilLog2(powerOfTwo + 1));
        }

        // INT_MAX = 2^31 - 1.
        Assert.Equal(31, Av1TileDecoder.CeilLog2(int.MaxValue));
    }
}
