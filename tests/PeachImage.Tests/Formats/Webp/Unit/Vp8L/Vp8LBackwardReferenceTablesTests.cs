using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LBackwardReferenceTablesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void DecodePrefixCodeValue_SmallSymbols_MapDirectlyWithoutConsumingExtraBits(int symbol, int expected)
    {
        byte[] data = [0xFF]; // if any bits were (incorrectly) consumed, later assertions in other tests would drift.
        var reader = new Vp8LBitReader(data, 0, data.Length);

        int value = Vp8LBackwardReferenceTables.DecodePrefixCodeValue(symbol, reader);

        Assert.Equal(expected, value);
    }

    [Fact]
    public void DecodePrefixCodeValue_Symbol4_ReadsOneExtraBit()
    {
        // extraBits=(4-2)>>1=1, offset=(2+(4&1))<<1=4 -> value = 4 + extraBitValue + 1.
        byte[] dataBitZero = [0b0000_0000];
        var readerZero = new Vp8LBitReader(dataBitZero, 0, dataBitZero.Length);
        Assert.Equal(5, Vp8LBackwardReferenceTables.DecodePrefixCodeValue(4, readerZero));

        byte[] dataBitOne = [0b0000_0001];
        var readerOne = new Vp8LBitReader(dataBitOne, 0, dataBitOne.Length);
        Assert.Equal(6, Vp8LBackwardReferenceTables.DecodePrefixCodeValue(4, readerOne));
    }

    [Fact]
    public void DecodePrefixCodeValue_Symbol6_ReadsTwoExtraBits()
    {
        // extraBits=(6-2)>>1=2, offset=(2+(6&1))<<2=8 -> value = 8 + extraBitValue + 1, extraBitValue in [0,3].
        byte[] data = [0b0000_0011]; // low 2 bits = 3 (LSB-first).
        var reader = new Vp8LBitReader(data, 0, data.Length);

        Assert.Equal(12, Vp8LBackwardReferenceTables.DecodePrefixCodeValue(6, reader));
    }

    [Fact]
    public void PlaneCodeToDistance_ShortCode_UsesSpatialNeighborhoodTable()
    {
        // Plane code 1 -> CodeToPlane[0] = 0x18 -> yOffset=1, xOffset=8-8=0 -> distance = 1*width + 0.
        Assert.Equal(10, Vp8LBackwardReferenceTables.PlaneCodeToDistance(width: 10, planeCode: 1));
    }

    [Fact]
    public void PlaneCodeToDistance_AnotherShortCode_MatchesHandComputedOffset()
    {
        // Plane code 2 -> CodeToPlane[1] = 0x07 -> yOffset=0, xOffset=8-7=1 -> distance = 0*width + 1 = 1.
        Assert.Equal(1, Vp8LBackwardReferenceTables.PlaneCodeToDistance(width: 10, planeCode: 2));
    }

    [Fact]
    public void PlaneCodeToDistance_CodeBeyondTable_IsARawDistanceOffset()
    {
        Assert.Equal(1, Vp8LBackwardReferenceTables.PlaneCodeToDistance(width: 640, planeCode: 121));
        Assert.Equal(500, Vp8LBackwardReferenceTables.PlaneCodeToDistance(width: 640, planeCode: 620));
    }
}
