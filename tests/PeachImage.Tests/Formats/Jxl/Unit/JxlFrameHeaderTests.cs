using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlFrameHeaderTests
{
    private static (JxlCodestreamHeaders Headers, JxlFrameHeader Frame, JxlToc Toc, int DataOffset, byte[] Codestream) ReadFirstFrame(string asset)
    {
        byte[] codestream = JxlContainer.Parse(JxlTestAssets.Load(asset)).Codestream.ToArray();
        var headers = JxlCodestreamHeaders.Read(codestream);
        var reader = new JxlBitReader(codestream.AsSpan(headers.FrameOffset));
        var frame = JxlFrameHeader.Read(ref reader, headers.Metadata, headers.Size);
        var dims = frame.Dimensions;
        var toc = JxlToc.Read(ref reader, JxlToc.EntryCount(dims.NumGroups, dims.NumDcGroups, frame.NumPasses));
        return (headers, frame, toc, headers.FrameOffset + (int)(reader.BitPosition / 8), codestream);
    }

    [Theory]
    [InlineData("rgb_lossless.jxl", true)]
    [InlineData("rgba.jxl", true)]
    [InlineData("gray.jxl", true)]
    [InlineData("rgb_lossy.jxl", false)]
    public void FirstFrameHeader_MatchesTheEncoderSettings(string asset, bool lossless)
    {
        var (headers, frame, toc, _, _) = ReadFirstFrame(asset);

        Assert.Equal(lossless ? JxlFrameEncoding.Modular : JxlFrameEncoding.VarDct, frame.Encoding);
        Assert.Equal(lossless ? JxlColorTransform.None : JxlColorTransform.Xyb, frame.ColorTransform);
        Assert.Equal(JxlFrameType.Regular, frame.FrameType);
        Assert.True(frame.IsLast);
        Assert.Equal(1, frame.Upsampling);
        Assert.Equal(JxlBlendMode.Replace, frame.Blending.Mode);
        Assert.Equal((int)headers.Size.Width, frame.Dimensions.XSizeUpsampled);
        Assert.Equal(1, frame.Dimensions.NumGroups);
        Assert.Equal(1, frame.NumPasses);

        // A one-group, one-pass frame has a single section whose id is zero.
        Assert.Single(toc.Sizes);
        Assert.Equal([0], toc.Ids);
    }

    [Fact]
    public void FrameSectionSizes_AccountForTheWholeFile()
    {
        var (_, _, toc, dataOffset, codestream) = ReadFirstFrame("rgb_lossless.jxl");

        Assert.Equal(codestream.Length, dataOffset + (long)toc.Sizes.Sum(s => (long)s));
    }

    [Fact]
    public void Dimensions_ComputeGroupGeometry()
    {
        // 1000x600 with 256-pixel groups: 4x3 groups; 125x75 blocks; one DC group.
        var dims = new JxlFrameDimensions(1000, 600, groupSizeShift: 1, maxHShift: 0, maxVShift: 0, modular: false, upsampling: 1);

        Assert.Equal(256, dims.GroupDimension);
        Assert.Equal(4, dims.XSizeGroups);
        Assert.Equal(3, dims.YSizeGroups);
        Assert.Equal(12, dims.NumGroups);
        Assert.Equal(125, dims.XSizeBlocks);
        Assert.Equal(75, dims.YSizeBlocks);
        Assert.Equal(1000, dims.XSizePadded);
        Assert.Equal(1, dims.NumDcGroups);
        Assert.Equal(2 + 1 + 12, JxlToc.EntryCount(dims.NumGroups, dims.NumDcGroups, 1));
        Assert.Equal(2 + 1 + 24, JxlToc.EntryCount(dims.NumGroups, dims.NumDcGroups, 2));
        Assert.Equal(1, JxlToc.EntryCount(1, 1, 1));
    }
}
