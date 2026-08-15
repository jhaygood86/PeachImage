using PeachImage.Formats.Bmp;
using PeachImage.Formats.Bmp.Decoding;

namespace PeachImage.Tests.Formats.Bmp.Unit.Decoding;

public class BmpRowUnpackerTests
{
    [Fact]
    public void UnpackBitsToIndices_1Bpp_IsMsbFirst()
    {
        byte[] row = [0b10110010]; // Pixels, MSB first: 1,0,1,1,0,0,1,0
        Span<byte> indices = stackalloc byte[8];

        BmpRowUnpacker.UnpackBitsToIndices(row, 1, indices);

        Assert.Equal([1, 0, 1, 1, 0, 0, 1, 0], indices.ToArray());
    }

    [Fact]
    public void UnpackBitsToIndices_4Bpp_IsHighNibbleFirst()
    {
        byte[] row = [0xA5, 0x3C]; // Pixel order: 0xA, 0x5, 0x3, 0xC
        Span<byte> indices = stackalloc byte[4];

        BmpRowUnpacker.UnpackBitsToIndices(row, 4, indices);

        Assert.Equal([0xA, 0x5, 0x3, 0xC], indices.ToArray());
    }

    [Fact]
    public void UnpackBitsToIndices_8Bpp_IsDirectCopy()
    {
        byte[] row = [5, 200, 17, 0, 255, 1]; // Includes 1 byte of row padding beyond width=5.
        Span<byte> indices = stackalloc byte[5];

        BmpRowUnpacker.UnpackBitsToIndices(row, 8, indices);

        Assert.Equal([5, 200, 17, 0, 255], indices.ToArray());
    }

    [Fact]
    public void ResolveIndexRow_LooksUpPaletteInRgbOrder()
    {
        byte[] indices = [0, 1];
        byte[] palette = [10, 20, 30, 40, 50, 60]; // Entry 0 = (10,20,30), entry 1 = (40,50,60).
        Span<byte> destRow = stackalloc byte[6];

        BmpRowUnpacker.ResolveIndexRow(indices, palette, destRow);

        Assert.Equal([10, 20, 30, 40, 50, 60], destRow.ToArray());
    }

    [Fact]
    public void ResolveIndexRow_OutOfRangeIndex_Throws()
    {
        byte[] indices = [5];
        byte[] palette = [1, 2, 3]; // Only 1 entry.
        byte[] destRow = new byte[3];

        Assert.Throws<BmpDecodingException>(() => BmpRowUnpacker.ResolveIndexRow(indices, palette, destRow));
    }

    [Fact]
    public void UnpackDirectColorRow_24Bpp_SwapsBgrToRgb()
    {
        byte[] row = [10, 20, 30]; // B, G, R
        Span<byte> destRow = stackalloc byte[3];

        BmpRowUnpacker.UnpackDirectColorRow(row, 24, default, hasAlpha: false, destRow);

        Assert.Equal([30, 20, 10], destRow.ToArray());
    }

    [Fact]
    public void UnpackDirectColorRow_16Bpp_UsesDefaultX1R5G5B5WhenNoMasksDeclared()
    {
        // All-white pixel in X1R5G5B5: 0111 1111 1111 1111 = 0x7FFF.
        byte[] row = [0xFF, 0x7F];
        var header = new BmpHeader { BitCount = 16 };
        Span<byte> destRow = stackalloc byte[3];

        BmpRowUnpacker.UnpackDirectColorRow(row, 16, header, hasAlpha: false, destRow);

        Assert.Equal([255, 255, 255], destRow.ToArray());
    }

    [Fact]
    public void UnpackDirectColorRow_32Bpp_DefaultMasks_ExtractsRgbAndIgnoresFourthByte()
    {
        byte[] row = [30, 20, 10, 0xAA]; // B, G, R, unused
        var header = new BmpHeader { BitCount = 32 };
        Span<byte> destRow = stackalloc byte[3];

        BmpRowUnpacker.UnpackDirectColorRow(row, 32, header, hasAlpha: false, destRow);

        Assert.Equal([10, 20, 30], destRow.ToArray());
    }

    [Fact]
    public void UnpackDirectColorRow_32Bpp_WithAlphaMask_ExtractsAlpha()
    {
        byte[] row = [30, 20, 10, 128]; // B, G, R, A
        var header = new BmpHeader { BitCount = 32, RMask = 0x00FF0000, GMask = 0x0000FF00, BMask = 0x000000FF, AMask = 0xFF000000 };
        Span<byte> destRow = stackalloc byte[4];

        BmpRowUnpacker.UnpackDirectColorRow(row, 32, header, hasAlpha: true, destRow);

        Assert.Equal([10, 20, 30, 128], destRow.ToArray());
    }

    [Fact]
    public void UnpackDirectColorRow_32Bpp_NonStandardMaskOrder_StillExtractsCorrectly()
    {
        // ABGR32: A@0xFF000000, B@0x00FF0000, G@0x0000FF00, R@0x000000FF — reversed from the usual layout.
        byte[] row = [10, 20, 30, 128]; // R, G, B, A in this custom byte order.
        var header = new BmpHeader { BitCount = 32, RMask = 0x000000FF, GMask = 0x0000FF00, BMask = 0x00FF0000, AMask = 0xFF000000 };
        Span<byte> destRow = stackalloc byte[4];

        BmpRowUnpacker.UnpackDirectColorRow(row, 32, header, hasAlpha: true, destRow);

        Assert.Equal([10, 20, 30, 128], destRow.ToArray());
    }
}
