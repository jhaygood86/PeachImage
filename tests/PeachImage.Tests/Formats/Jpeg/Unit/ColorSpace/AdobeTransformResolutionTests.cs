using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Decoding.ColorSpace;
using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Tests.Formats.Jpeg.Unit.ColorSpace;

public class AdobeTransformResolutionTests
{
    [Fact]
    public void OneComponent_IsAlwaysGrayscale()
    {
        var (colorSpace, inverted) = ColorSpaceResolver.Resolve(1, adobe: null);
        Assert.Equal(JpegColorSpace.Grayscale, colorSpace);
        Assert.False(inverted);
    }

    [Fact]
    public void ThreeComponents_WithoutAdobeMarker_IsYCbCr()
    {
        var (colorSpace, _) = ColorSpaceResolver.Resolve(3, adobe: null);
        Assert.Equal(JpegColorSpace.YCbCr, colorSpace);
    }

    [Fact]
    public void ThreeComponents_WithAdobeTransformZero_IsDirectRgb()
    {
        var (colorSpace, _) = ColorSpaceResolver.Resolve(3, new JpegAdobeSegment(transform: 0));
        Assert.Equal(JpegColorSpace.Rgb, colorSpace);
    }

    [Fact]
    public void ThreeComponents_WithAdobeTransformOne_IsYCbCr()
    {
        var (colorSpace, _) = ColorSpaceResolver.Resolve(3, new JpegAdobeSegment(transform: 1));
        Assert.Equal(JpegColorSpace.YCbCr, colorSpace);
    }

    [Fact]
    public void FourComponents_WithoutAdobeMarker_IsDirectCmykNotInverted()
    {
        var (colorSpace, inverted) = ColorSpaceResolver.Resolve(4, adobe: null);
        Assert.Equal(JpegColorSpace.Cmyk, colorSpace);
        Assert.False(inverted);
    }

    [Fact]
    public void FourComponents_WithAdobeTransformZero_IsDirectCmykInverted()
    {
        var (colorSpace, inverted) = ColorSpaceResolver.Resolve(4, new JpegAdobeSegment(transform: 0));
        Assert.Equal(JpegColorSpace.Cmyk, colorSpace);
        Assert.True(inverted);
    }

    [Fact]
    public void FourComponents_WithAdobeTransformTwo_IsYcck()
    {
        var (colorSpace, inverted) = ColorSpaceResolver.Resolve(4, new JpegAdobeSegment(transform: 2));
        Assert.Equal(JpegColorSpace.Ycck, colorSpace);

        // Inversion for YCCK is inherent in the YCbCr->CMY transform itself, not a separate post-process step.
        Assert.False(inverted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void UnsupportedComponentCount_ThrowsJpegDecodingException(int componentCount)
    {
        Assert.Throws<JpegDecodingException>(() => ColorSpaceResolver.Resolve(componentCount, adobe: null));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void ThreeComponents_WithUnsupportedAdobeTransform_ThrowsJpegDecodingException(byte transform)
    {
        Assert.Throws<JpegDecodingException>(() => ColorSpaceResolver.Resolve(3, new JpegAdobeSegment(transform)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void FourComponents_WithUnsupportedAdobeTransform_ThrowsJpegDecodingException(byte transform)
    {
        Assert.Throws<JpegDecodingException>(() => ColorSpaceResolver.Resolve(4, new JpegAdobeSegment(transform)));
    }

    [Fact]
    public void AdobeSegment_TryParse_RejectsPayloadWithoutSignature()
    {
        byte[] payload = new byte[12];
        "NotAdobe"u8[..8].CopyTo(payload);
        Assert.False(JpegAdobeSegment.TryParse(payload, out _));
    }

    [Fact]
    public void AdobeSegment_TryParse_AcceptsValidSignature()
    {
        byte[] payload = new byte[12];
        "Adobe"u8.CopyTo(payload);
        payload[5] = 0x00;
        payload[6] = 0x64;
        payload[7] = 0x40;
        payload[8] = 0x00;
        payload[9] = 0x00;
        payload[10] = 0x00;
        payload[11] = 2;

        Assert.True(JpegAdobeSegment.TryParse(payload, out var segment));
        Assert.Equal(2, segment.Transform);
        Assert.Equal(0x0064, segment.DctEncodeVersion);
        Assert.Equal(0x4000, segment.Flags0);
        Assert.Equal(0x0000, segment.Flags1);
    }
}
