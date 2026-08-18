using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Chunks;

public class HeaderOverflowTests
{
    [Fact]
    public void Decode_IhdrWidthOverflowsInt32_ThrowsPngDecodingException_NotOverflowException()
    {
        // int.MinValue's raw bytes are 0x80000000 (2,147,483,648) when reinterpreted as the IHDR width
        // field's unsigned big-endian uint32 — one past Int32.MaxValue. The `checked((int)...)` cast used to
        // let a raw OverflowException escape instead of PngDecodingException, breaking Image.TryLoad's
        // documented "only ImageFormatException" contract.
        byte[] file = PngTestFileBuilder.Build(width: int.MinValue, height: 1, bitDepth: 8, colorType: 0,
            palette: null, trns: null, scanlines: [[0, 10]]);

        Assert.Throws<PngDecodingException>(() => PngDecoder.Decode(new MemoryStream(file)));
    }

    [Fact]
    public void Decode_16BitRgbaAtMaxPixelCount_ThrowsPngDecodingException_NotOverflowException()
    {
        // 16384 x 16384 = 268,435,456 pixels, exactly PngDecodingLimits.MaxPixelCount — passes the IHDR-level
        // pixel-count guard, which doesn't account for pixel format byte size. Grayscale + 16-bit + a tRNS
        // chunk selects PixelFormat.Rgba64 (8 bytes/pixel): 268,435,456 * 8 = 2,147,483,648, one past
        // Int32.MaxValue, which used to overflow Image.Create's byte-count math into a raw OverflowException
        // rather than PngDecodingException. Decode throws before ever reading IDAT pixel data, so the
        // declared dimensions don't need genuinely-sized scanline content to reach the bug.
        byte[] file = PngTestFileBuilder.Build(width: 16384, height: 16384, bitDepth: 16, colorType: 0,
            palette: null, trns: [0, 0], scanlines: [[0]]);

        Assert.Throws<PngDecodingException>(() => PngDecoder.Decode(new MemoryStream(file)));
    }

    [Fact]
    public void Decode_OrdinaryFile_StillDecodesNormally()
    {
        byte[] file = PngTestFileBuilder.Build(width: 2, height: 2, bitDepth: 8, colorType: 0,
            palette: null, trns: null, scanlines: [[0, 10, 20], [0, 30, 40]]);

        var image = PngDecoder.Decode(new MemoryStream(file));

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
    }
}
