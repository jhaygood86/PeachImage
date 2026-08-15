using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Decoding;

public class BmpBitfieldMaskTests
{
    [Theory]
    [InlineData(0x001Fu, 0, 5)] // 5-bit blue, shift 0 (X1R5G5B5)
    [InlineData(0x03E0u, 5, 5)] // 5-bit green, shift 5
    [InlineData(0x7C00u, 10, 5)] // 5-bit red, shift 10
    [InlineData(0xF800u, 11, 5)] // 5-bit red, 5-6-5 layout
    [InlineData(0x07E0u, 5, 6)] // 6-bit green, 5-6-5 layout
    [InlineData(0x000000FFu, 0, 8)]
    [InlineData(0xFF000000u, 24, 8)]
    [InlineData(0x0u, 0, 0)]
    public void Analyze_ComputesShiftAndBitCount(uint mask, int expectedShift, int expectedBitCount)
    {
        var info = BmpBitfieldMask.Analyze(mask);

        Assert.Equal(expectedShift, info.Shift);
        Assert.Equal(expectedBitCount, info.BitCount);
    }

    [Fact]
    public void Scale_ZeroBitCount_ReturnsZero_NoDivideByZero()
    {
        var info = BmpBitfieldMask.Analyze(0);
        byte result = BmpBitfieldMask.Scale(12345, info);

        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData(31, 5, 255)] // Max 5-bit value scales to max byte.
    [InlineData(0, 5, 0)]
    [InlineData(15, 5, 123)] // Midpoint, rounded.
    [InlineData(255, 8, 255)] // 8-bit mask is an identity scale.
    [InlineData(0, 8, 0)]
    [InlineData(128, 8, 128)]
    public void Scale_LinearlyScalesToEightBits(uint rawValue, int bitCount, byte expected)
    {
        var info = new BmpBitfieldMask.MaskInfo(0, bitCount);
        byte result = BmpBitfieldMask.Scale(rawValue, info);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Extract_PullsFieldOutOfPackedValueBeforeScaling()
    {
        // 5-6-5: R=0xF800 (shift 11), G=0x07E0 (shift 5), B=0x001F (shift 0).
        uint packed = 0b11111_000000_00000; // Max red, zero green/blue.
        var rInfo = BmpBitfieldMask.Analyze(0xF800);

        byte red = BmpBitfieldMask.Extract(packed, 0xF800, rInfo);

        Assert.Equal(255, red);
    }

    [Fact]
    public void ResolveEffectiveMasks_UsesDefaultX1R5G5B5_When16BppAndNoMasksDeclared()
    {
        var header = new BmpHeader { BitCount = 16, RMask = 0, GMask = 0, BMask = 0 };

        var (r, g, b, a) = BmpBitfieldMask.ResolveEffectiveMasks(header);

        Assert.Equal(0x7C00u, r);
        Assert.Equal(0x03E0u, g);
        Assert.Equal(0x001Fu, b);
        Assert.Equal(0u, a);
    }

    [Fact]
    public void ResolveEffectiveMasks_UsesDefaultByteAlignedMasks_When32BppAndNoMasksDeclared()
    {
        var header = new BmpHeader { BitCount = 32, RMask = 0, GMask = 0, BMask = 0 };

        var (r, g, b, _) = BmpBitfieldMask.ResolveEffectiveMasks(header);

        Assert.Equal(0x00FF0000u, r);
        Assert.Equal(0x0000FF00u, g);
        Assert.Equal(0x000000FFu, b);
    }

    [Fact]
    public void ResolveEffectiveMasks_PrefersDeclaredMasksOverDefaults()
    {
        var header = new BmpHeader { BitCount = 16, RMask = 0xF800, GMask = 0x07E0, BMask = 0x001F, AMask = 0 };

        var (r, g, b, _) = BmpBitfieldMask.ResolveEffectiveMasks(header);

        Assert.Equal(0xF800u, r);
        Assert.Equal(0x07E0u, g);
        Assert.Equal(0x001Fu, b);
    }
}
