using System.Buffers.Binary;
using PeachImage.Formats.Gif;
using PeachImage.Formats.Gif.Decoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Decoding;

public class GifSingleFrameDecoderCanvasLimitTests
{
    [Fact]
    public void Decode_LogicalScreenExceedsCanvasCap_ThrowsBeforeAllocating()
    {
        // GifSingleFrameDecoder.Decode allocates at the *logical screen's* declared dimensions, not the
        // actual first frame's size — a tiny file can declare a huge logical screen and force a large
        // allocation regardless of how little real frame/compressed data follows. 2048x2048 RGBA32 = 16MB;
        // a cap of 1MB must reject it before Image.Create is ever called.
        byte[] gif = BuildMinimalGif(screenWidth: 2048, screenHeight: 2048);

        Assert.Throws<GifDecodingException>(() => GifSingleFrameDecoder.Decode(new MemoryStream(gif), maxInitialCanvasBytes: 1024 * 1024));
    }

    [Fact]
    public void Decode_LogicalScreenWithinCanvasCap_DecodesNormally()
    {
        byte[] gif = BuildMinimalGif(screenWidth: 4, screenHeight: 4);

        using var image = GifSingleFrameDecoder.Decode(new MemoryStream(gif), maxInitialCanvasBytes: 1024 * 1024);

        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
    }

    [Fact]
    public void Decode_OrdinaryFile_UsesProductionDefaultAndDecodesNormally()
    {
        byte[] gif = BuildMinimalGif(screenWidth: 4, screenHeight: 4);

        using var image = GifSingleFrameDecoder.Decode(new MemoryStream(gif));

        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
    }

    /// <summary>
    /// Builds a minimal valid GIF with the given logical screen size and a single opaque 1x1 frame using a
    /// 2-color global palette — small enough that only the *declared* screen dimensions, not the actual frame
    /// content, could plausibly drive a large allocation.
    /// </summary>
    private static byte[] BuildMinimalGif(int screenWidth, int screenHeight)
    {
        using var ms = new MemoryStream();

        ms.Write("GIF89a"u8);

        Span<byte> screenDescriptor = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(screenDescriptor[0..2], (ushort)screenWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(screenDescriptor[2..4], (ushort)screenHeight);
        screenDescriptor[4] = 0b1000_0000; // global color table present, 2 entries (2^(0+1)).
        screenDescriptor[5] = 0; // background color index
        screenDescriptor[6] = 0; // pixel aspect ratio
        ms.Write(screenDescriptor);

        ms.Write([0, 0, 0, 255, 255, 255]); // 2-entry global color table: black, white.

        ms.WriteByte(0x2C); // image separator
        Span<byte> imageDescriptor = stackalloc byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(imageDescriptor[0..2], 0); // left
        BinaryPrimitives.WriteUInt16LittleEndian(imageDescriptor[2..4], 0); // top
        BinaryPrimitives.WriteUInt16LittleEndian(imageDescriptor[4..6], 1); // width
        BinaryPrimitives.WriteUInt16LittleEndian(imageDescriptor[6..8], 1); // height
        imageDescriptor[8] = 0; // no local color table, not interlaced
        ms.Write(imageDescriptor);

        ms.WriteByte(2); // LZW minimum code size
        ms.WriteByte(2); // sub-block length
        ms.Write([0x44, 0x01]); // clear code + index 0 + EOI, minimal valid LZW stream for a 1-pixel image
        ms.WriteByte(0); // block terminator

        ms.WriteByte(0x3B); // trailer

        return ms.ToArray();
    }
}
