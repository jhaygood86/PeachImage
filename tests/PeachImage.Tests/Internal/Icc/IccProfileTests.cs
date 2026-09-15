using PeachImage.Internal.Icc;
using PeachImage.Tests.Formats.Jpeg.Corpus;

namespace PeachImage.Tests.Internal.Icc;

/// <summary>
/// Exercises the ported ICC engine directly against a real, already-fetched embedded profile: the Imazen
/// codec-corpus fixture <c>ycck.jpg</c> carries a genuine Adobe-authored v2.1 "prtr"-class CMYK profile with a
/// Lab PCS and legacy <c>mft1</c>/<c>mft2</c> LUT tags (confirmed by direct inspection during planning) — a
/// real-world profile shape, not a synthetic one. These assert physically-meaningful invariants that don't
/// require an external reference color-management module.
/// </summary>
public class IccProfileTests
{
    private static byte[]? LoadYcckIccProfile()
    {
        string path = Path.Combine(CorpusPaths.ImazenRoot, "jpeg-conformance", "valid", "ycck.jpg");
        if (!CorpusFixture.IsAvailable || !File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        using var image = PeachImage.Formats.Jpeg.JpegDecoder.Decode(stream);
        foreach (var profile in image.Metadata.Profiles)
        {
            if (profile.Kind == MetadataProfileKind.Icc)
            {
                return profile.Data;
            }
        }

        return null;
    }

    [Fact]
    public void YcckFixture_ParsesAsSupportedCmykProfile()
    {
        byte[]? bytes = LoadYcckIccProfile();
        if (bytes is null)
        {
            Assert.Skip("External JPEG test corpus is not available, or ycck.jpg has no embedded ICC profile.");
        }

        var profile = new IccProfile(bytes);
        profile.ErrorIfUnsupported(); // Must not throw.
        Assert.Equal(IccSignatures.Cmyk, profile.DataColorSpace);
    }

    [Fact]
    public void YcckFixture_PureWhiteDeviceValues_MapNearWhite()
    {
        byte[]? bytes = LoadYcckIccProfile();
        if (bytes is null)
        {
            Assert.Skip("External JPEG test corpus is not available, or ycck.jpg has no embedded ICC profile.");
        }

        var profile = new IccProfile(bytes);
        var intent = profile.DefaultIntent == IccIntent.Unspecified ? IccIntent.Perceptual : profile.DefaultIntent;

        // C = M = Y = K = 0 is "no ink" -- the printed page's own white point, which should encode as a bright,
        // roughly neutral color once converted all the way to sRGB.
        var xyz = profile.ToXyzD50([0.0, 0.0, 0.0, 0.0], intent);
        var linear = IccColorMath.XyzD50ToLinearSrgb.Multiply(xyz);
        byte r = IccColorMath.LinearToSrgbByte(linear.X);
        byte g = IccColorMath.LinearToSrgbByte(linear.Y);
        byte b = IccColorMath.LinearToSrgbByte(linear.Z);

        Assert.True(r > 200, $"Expected a bright red channel for paper white, got {r}.");
        Assert.True(g > 200, $"Expected a bright green channel for paper white, got {g}.");
        Assert.True(b > 200, $"Expected a bright blue channel for paper white, got {b}.");
    }

    [Fact]
    public void YcckFixture_PureBlackDeviceValues_MapNearBlack()
    {
        byte[]? bytes = LoadYcckIccProfile();
        if (bytes is null)
        {
            Assert.Skip("External JPEG test corpus is not available, or ycck.jpg has no embedded ICC profile.");
        }

        var profile = new IccProfile(bytes);
        var intent = profile.DefaultIntent == IccIntent.Unspecified ? IccIntent.Perceptual : profile.DefaultIntent;

        // K = 1.0 (full black ink), C = M = Y = 0 is the darkest a real press typically produces.
        var xyz = profile.ToXyzD50([0.0, 0.0, 0.0, 1.0], intent);
        var linear = IccColorMath.XyzD50ToLinearSrgb.Multiply(xyz);
        byte r = IccColorMath.LinearToSrgbByte(linear.X);
        byte g = IccColorMath.LinearToSrgbByte(linear.Y);
        byte b = IccColorMath.LinearToSrgbByte(linear.Z);

        Assert.True(r < 60, $"Expected a dark red channel for full-K black, got {r}.");
        Assert.True(g < 60, $"Expected a dark green channel for full-K black, got {g}.");
        Assert.True(b < 60, $"Expected a dark blue channel for full-K black, got {b}.");
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, 0.0)]
    [InlineData(0.0, 0.0, 0.0, 0.5)]
    [InlineData(0.3, 0.6, 0.1, 0.2)]
    public void YcckFixture_PcsToDeviceToPcsRoundTrip_StaysCloseToOriginalPcsValue(double c, double m, double y, double k)
    {
        // NOTE: a device->PCS->device round trip (AToB0 then BToA0) is *not* expected to return the original
        // device values for a real CMYK profile -- CMYK is redundant for driving 3D colorimetry (many device
        // combinations can produce the same visible color, e.g. via different black-generation/GCR choices),
        // so AToB0 and BToA0 are independently-generated LUTs, not true mathematical inverses of each other.
        // Confirmed empirically against this exact fixture: C=M=Y=0,K=0.5 round-tripped to C~0.44, not ~0.
        // The direction that *is* expected to hold is PCS->device->PCS: a specific achievable color, read back
        // through the profile after being turned into ink, should reproduce close to that same color.
        byte[]? bytes = LoadYcckIccProfile();
        if (bytes is null)
        {
            Assert.Skip("External JPEG test corpus is not available, or ycck.jpg has no embedded ICC profile.");
        }

        var profile = new IccProfile(bytes);
        var intent = profile.DefaultIntent == IccIntent.Unspecified ? IccIntent.Perceptual : profile.DefaultIntent;

        var originalXyz = profile.ToXyzD50([c, m, y, k], intent);

        Span<double> device = stackalloc double[4];
        profile.FromXyzD50(originalXyz, intent, device);
        var roundTrippedXyz = profile.ToXyzD50(device, intent);

        Assert.True(Math.Abs(originalXyz.X - roundTrippedXyz.X) < 0.05, $"X: original={originalXyz.X}, round-tripped={roundTrippedXyz.X}.");
        Assert.True(Math.Abs(originalXyz.Y - roundTrippedXyz.Y) < 0.05, $"Y: original={originalXyz.Y}, round-tripped={roundTrippedXyz.Y}.");
        Assert.True(Math.Abs(originalXyz.Z - roundTrippedXyz.Z) < 0.05, $"Z: original={originalXyz.Z}, round-tripped={roundTrippedXyz.Z}.");
    }

    // IccTransformAToB is exercised above via ycck.jpg's real embedded profile. IccTransformTrcMatrix and
    // IccTransformTrcGrey have no real-world corpus fixture, so these use hand-built synthetic profiles
    // (SyntheticIccProfileBuilder) instead -- the only way to close this coverage gap deterministically.

    [Fact]
    public void SyntheticRgbTrcMatrixProfile_ParsesAsSupportedRgbProfile()
    {
        var profile = new IccProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        profile.ErrorIfUnsupported(); // Must not throw.
        Assert.Equal(IccSignatures.Rgb, profile.DataColorSpace);
        Assert.Equal(3, profile.ChannelCount);
    }

    [Fact]
    public void SyntheticRgbTrcMatrixProfile_WhiteAndBlackMapAsExpected()
    {
        var profile = new IccProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var intent = profile.DefaultIntent == IccIntent.Unspecified ? IccIntent.Perceptual : profile.DefaultIntent;

        var white = profile.ToXyzD50([1.0, 1.0, 1.0], intent);
        var whiteRgb = IccColorMath.XyzD50ToLinearSrgb.Multiply(white);
        Assert.True(IccColorMath.LinearToSrgbByte(whiteRgb.X) > 200);
        Assert.True(IccColorMath.LinearToSrgbByte(whiteRgb.Y) > 200);
        Assert.True(IccColorMath.LinearToSrgbByte(whiteRgb.Z) > 200);

        var black = profile.ToXyzD50([0.0, 0.0, 0.0], intent);
        var blackRgb = IccColorMath.XyzD50ToLinearSrgb.Multiply(black);
        Assert.True(IccColorMath.LinearToSrgbByte(blackRgb.X) < 20);
        Assert.True(IccColorMath.LinearToSrgbByte(blackRgb.Y) < 20);
        Assert.True(IccColorMath.LinearToSrgbByte(blackRgb.Z) < 20);
    }

    [Fact]
    public void SyntheticGrayTrcProfile_ParsesAsSupportedGrayProfile()
    {
        var profile = new IccProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());
        profile.ErrorIfUnsupported(); // Must not throw.
        Assert.Equal(IccSignatures.Grey, profile.DataColorSpace);
        Assert.Equal(1, profile.ChannelCount);
    }

    [Fact]
    public void SyntheticGrayTrcProfile_WhiteAndBlackMapAsExpected()
    {
        var profile = new IccProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());
        var intent = profile.DefaultIntent == IccIntent.Unspecified ? IccIntent.Perceptual : profile.DefaultIntent;

        var white = profile.ToXyzD50([1.0], intent);
        var whiteRgb = IccColorMath.XyzD50ToLinearSrgb.Multiply(white);
        Assert.True(IccColorMath.LinearToSrgbByte(whiteRgb.Y) > 200);

        var black = profile.ToXyzD50([0.0], intent);
        var blackRgb = IccColorMath.XyzD50ToLinearSrgb.Multiply(black);
        Assert.True(IccColorMath.LinearToSrgbByte(blackRgb.Y) < 20);
    }
}
