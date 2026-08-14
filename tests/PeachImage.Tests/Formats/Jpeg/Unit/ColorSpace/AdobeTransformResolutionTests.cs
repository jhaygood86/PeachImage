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
        payload[11] = 2;

        Assert.True(JpegAdobeSegment.TryParse(payload, out var segment));
        Assert.Equal(2, segment.Transform);
    }
}
