using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Encoder.Container;
using PeachImage.Formats.Jxl.Encoder.Headers;
using PeachImage.Formats.Jxl.Headers;
using ProductionWriter = PeachImage.Formats.Jxl.Encoder.Bitstream.JxlBitWriter;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>Round-trips the encoder's bit writer, header writers and container writer through the decoder's readers.</summary>
public class JxlEncoderInfrastructureTests
{
    [Fact]
    public void BitWriter_MatchesTestWriterBitOrder()
    {
        var production = new ProductionWriter();
        var reference = new JxlBitWriter();
        (ulong Value, int Count)[] fields = [(1, 1), (0x2A, 6), (0x1FFFF, 17), (0, 3), (0xDEADBEEFCAFEUL, 48), (0x123456789ABCDEFUL, 60)];
        foreach (var (value, count) in fields)
        {
            production.WriteBits(value, count);
            reference.WriteBits(value, count);
        }

        Assert.Equal(reference.ToArray(), production.ToArray());
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(17u)]
    [InlineData(511u)]
    [InlineData(512u)]
    [InlineData(70000u)]
    [InlineData(1073741823u)]
    public void U32_RoundTripsThroughFieldReader(uint value)
    {
        var writer = new ProductionWriter();
        writer.WriteU32(value, U32Dist.BitsOffset(9, 1), U32Dist.BitsOffset(13, 1), U32Dist.BitsOffset(18, 1), U32Dist.BitsOffset(30, 1));
        writer.WriteU32(value & 0xFF, U32Dist.Val(0), U32Dist.Bits(8), U32Dist.BitsOffset(10, 256), U32Dist.BitsOffset(20, 0));

        var reader = new JxlBitReader(writer.ToArray());
        Assert.Equal(value, JxlFieldReader.ReadU32(ref reader, U32Dist.BitsOffset(9, 1), U32Dist.BitsOffset(13, 1), U32Dist.BitsOffset(18, 1), U32Dist.BitsOffset(30, 1)));
        Assert.Equal(value & 0xFF, JxlFieldReader.ReadU32(ref reader, U32Dist.Val(0), U32Dist.Bits(8), U32Dist.BitsOffset(10, 256), U32Dist.BitsOffset(20, 0)));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(5UL)]
    [InlineData(16UL)]
    [InlineData(17UL)]
    [InlineData(272UL)]
    [InlineData(273UL)]
    [InlineData(4095UL)]
    [InlineData(4096UL)]
    [InlineData(1UL << 40)]
    [InlineData(ulong.MaxValue)]
    public void U64_RoundTripsThroughFieldReader(ulong value)
    {
        var writer = new ProductionWriter();
        writer.WriteU64(value);
        var reader = new JxlBitReader(writer.ToArray());
        Assert.Equal(value, JxlFieldReader.ReadU64(ref reader));
    }

    [Fact]
    public void EnumF16AndName_RoundTrip()
    {
        var writer = new ProductionWriter();
        writer.WriteEnum(13);
        writer.WriteEnum(0);
        writer.WriteF16(0.5f);
        writer.WriteName("alpha ✓");

        var reader = new JxlBitReader(writer.ToArray());
        Assert.Equal(13u, JxlFieldReader.ReadEnum(ref reader));
        Assert.Equal(0u, JxlFieldReader.ReadEnum(ref reader));
        Assert.Equal(0.5f, JxlFieldReader.ReadF16(ref reader));
        Assert.Equal("alpha ✓", JxlFieldReader.ReadName(ref reader));
    }

    [Theory]
    [InlineData(8u, 8u, 8, false, false, true, 1)]
    [InlineData(64u, 48u, 8, false, false, false, 1)]
    [InlineData(256u, 256u, 8, true, false, false, 1)]
    [InlineData(1u, 1u, 8, false, true, false, 1)]
    [InlineData(1920u, 1080u, 16, false, true, true, 6)]
    [InlineData(300u, 200u, 16, true, true, false, 8)]
    [InlineData(5u, 3u, 10, false, false, false, 1)]
    [InlineData(100000u, 7u, 8, false, false, true, 3)]
    public void CodestreamHeaders_RoundTripThroughDecoder(uint width, uint height, int bits, bool gray, bool alpha, bool xyb, int orientation)
    {
        var writer = new ProductionWriter();
        JxlCodestreamHeaderWriter.Write(writer, new JxlEncodedImageInfo(width, height, bits, gray, alpha, xyb, orientation));

        var headers = JxlCodestreamHeaders.Read(writer.ToArray());

        Assert.Equal(new JxlSize(width, height), headers.Size);
        var metadata = headers.Metadata;
        Assert.Equal((uint)bits, metadata.BitDepth.BitsPerSample);
        Assert.False(metadata.BitDepth.IsFloat);
        Assert.Equal(xyb, metadata.XybEncoded);
        Assert.Equal(orientation, metadata.Orientation);
        Assert.Equal(alpha ? 0 : -1, metadata.AlphaChannelIndex);
        Assert.Equal(gray ? JxlColorSpace.Gray : JxlColorSpace.Rgb, metadata.ColorEncoding.ColorSpace);
        Assert.False(metadata.ColorEncoding.WantIcc);
        Assert.Equal(JxlTransferFunction.Srgb, metadata.ColorEncoding.TransferFunction);
        Assert.Equal(writer.ToArray().Length, headers.FrameOffset);
    }

    [Fact]
    public void Container_WithoutMetadata_IsBareCodestream()
    {
        byte[] codestream = [0xFF, 0x0A, 1, 2, 3];
        using var stream = new MemoryStream();
        JxlContainerWriter.Write(stream, codestream, exif: null, xmp: null);
        Assert.Equal(codestream, stream.ToArray());
    }

    [Fact]
    public void Container_WithMetadata_RoundTripsThroughParser()
    {
        byte[] codestream = [0xFF, 0x0A, 9, 8, 7, 6];
        byte[] exif = [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8];
        byte[] xmp = "<x:xmpmeta/>"u8.ToArray();
        using var stream = new MemoryStream();
        JxlContainerWriter.Write(stream, codestream, exif, xmp);

        var parsed = JxlContainer.Parse(stream.ToArray());

        Assert.Equal(codestream, parsed.Codestream.ToArray());
        Assert.Equal(exif, parsed.Exif);
        Assert.Equal(xmp, parsed.Xmp);
    }
}
