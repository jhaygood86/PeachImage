using PeachImage.Formats.Webp.Encoding;

namespace PeachImage.Tests.Formats.Webp.Unit.ContainerWriter;

public class WebpContainerWriterTests
{
    [Theory]
    [InlineData(false, (byte)0x00)]
    [InlineData(true, (byte)0x02)]
    public void BuildVp8XPayload_SetsAnimationBit(bool hasAnimation, byte expectedFlags)
    {
        byte[] payload = WebpContainerWriter.BuildVp8XPayload(10, 20, hasAlpha: false, hasAnimation, hasIcc: false, hasExif: false, hasXmp: false);

        Assert.Equal(expectedFlags, payload[0]);
        Assert.Equal(9, payload[4] | (payload[5] << 8) | (payload[6] << 16)); // width - 1
        Assert.Equal(19, payload[7] | (payload[8] << 8) | (payload[9] << 16)); // height - 1
    }

    [Fact]
    public void BuildVp8XPayload_CombinesAnimationWithOtherFlags()
    {
        byte[] payload = WebpContainerWriter.BuildVp8XPayload(4, 4, hasAlpha: true, hasAnimation: true, hasIcc: true, hasExif: false, hasXmp: false);

        Assert.Equal(0x20 | 0x10 | 0x02, payload[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(ushort.MaxValue)]
    public void BuildAnimPayload_RoundTripsLoopCount(int loopCount)
    {
        byte[] payload = WebpContainerWriter.BuildAnimPayload(loopCount);

        Assert.Equal(6, payload.Length);
        Assert.Equal(0, payload[0] | payload[1] | payload[2] | payload[3]); // transparent background
        Assert.Equal(loopCount, payload[4] | (payload[5] << 8));
    }

    [Fact]
    public void BuildAnimPayload_ClampsLoopCountToUInt16Range()
    {
        byte[] payload = WebpContainerWriter.BuildAnimPayload(loopCount: int.MaxValue);

        Assert.Equal(ushort.MaxValue, payload[4] | (payload[5] << 8));
    }

    [Theory]
    [InlineData(false, (byte)0x02)] // do-not-blend bit always set; dispose bit clear
    [InlineData(true, (byte)0x03)] // do-not-blend bit + dispose-to-background bit
    public void BuildAnmfFrameHeader_SetsFlagsAndAlwaysWritesDoNotBlend(bool disposeToBackground, byte expectedFlags)
    {
        byte[] header = WebpContainerWriter.BuildAnmfFrameHeader(width: 30, height: 40, durationMs: 1234, disposeToBackground);

        Assert.Equal(16, header.Length);
        Assert.Equal(0, header[0] | header[1] | header[2] | header[3] | header[4] | header[5]); // x = y = 0
        Assert.Equal(29, header[6] | (header[7] << 8) | (header[8] << 16)); // width - 1
        Assert.Equal(39, header[9] | (header[10] << 8) | (header[11] << 16)); // height - 1
        Assert.Equal(1234, header[12] | (header[13] << 8) | (header[14] << 16));
        Assert.Equal(expectedFlags, header[15]);
    }

    [Fact]
    public void BuildAnmfFrameHeader_ClampsDurationToTwentyFourBitRange()
    {
        byte[] header = WebpContainerWriter.BuildAnmfFrameHeader(width: 1, height: 1, durationMs: int.MaxValue, disposeToBackground: false);

        Assert.Equal(0xFF_FFFF, header[12] | (header[13] << 8) | (header[14] << 16));
    }
}
