using PeachImage.Tests.Internal.Icc;

namespace PeachImage.Tests;

/// <summary>
/// Tests the public <see cref="IccColorProfile"/> API surface directly (as a downstream library consumer
/// would use it), as opposed to <c>PeachImage.Tests.Internal.Icc.IccProfileTests</c>, which exercises the
/// internal engine underneath it.
/// </summary>
public class IccColorProfileTests
{
    [Fact]
    public void Constructor_ThrowsIccProfileException_ForTooShortBytes()
    {
        Assert.Throws<IccProfileException>(() => new IccColorProfile(new byte[10]));
    }

    [Fact]
    public void TryCreate_ReturnsFalse_ForTooShortBytes()
    {
        bool result = IccColorProfile.TryCreate(new byte[10], out var profile);
        Assert.False(result);
        Assert.Null(profile);
    }

    [Fact]
    public void RgbTrcMatrixProfile_ReportsExpectedShape()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        Assert.Equal(IccColorSpace.Rgb, profile.DataColorSpace);
        Assert.Equal(3, profile.ChannelCount);
        Assert.Equal(IccRenderingIntent.Perceptual, profile.DefaultRenderingIntent);
    }

    [Fact]
    public void GrayTrcProfile_ReportsExpectedShape()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());
        Assert.Equal(IccColorSpace.Gray, profile.DataColorSpace);
        Assert.Equal(1, profile.ChannelCount);
    }

    [Fact]
    public void ConvertToSrgb_RgbProfile_WhiteAndBlackMapAsExpected()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());

        Span<byte> deviceValues = [255, 255, 255, 0, 0, 0];
        Span<byte> destination = stackalloc byte[8];
        profile.ConvertToSrgb(deviceValues, destination, pixelCount: 2);

        Assert.True(destination[0] > 200 && destination[1] > 200 && destination[2] > 200, "Expected white device values to convert to a bright RGB pixel.");
        Assert.Equal(255, destination[3]);
        Assert.True(destination[4] < 20 && destination[5] < 20 && destination[6] < 20, "Expected black device values to convert to a dark RGB pixel.");
        Assert.Equal(255, destination[7]);
    }

    [Fact]
    public void ConvertToSrgb_GrayProfile_WhiteAndBlackMapAsExpected()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());

        Span<byte> deviceValues = [255, 0];
        Span<byte> destination = stackalloc byte[8];
        profile.ConvertToSrgb(deviceValues, destination, pixelCount: 2);

        Assert.True(destination[1] > 200, "Expected white to convert to a bright pixel.");
        Assert.True(destination[5] < 20, "Expected black to convert to a dark pixel.");
    }

    [Fact]
    public void ConvertToSrgb_WrongDeviceValuesLength_Throws()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var destination = new byte[4];
        Assert.Throws<ArgumentException>(() => profile.ConvertToSrgb(new byte[2], destination, pixelCount: 1));
    }

    [Fact]
    public void ConvertToSrgb_WrongDestinationLength_Throws()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var deviceValues = new byte[3];
        Assert.Throws<ArgumentException>(() => profile.ConvertToSrgb(deviceValues, new byte[3], pixelCount: 1));
    }

    [Fact]
    public void ConvertToSrgb_NegativePixelCount_Throws()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.ConvertToSrgb([], [], pixelCount: -1));
    }

    [Fact]
    public void ConvertTo_RgbToGray_WhiteAndBlackMapAsExpected()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());

        Span<byte> deviceValues = [255, 255, 255, 0, 0, 0];
        Span<byte> destinationValues = stackalloc byte[2];
        source.ConvertTo(destination, deviceValues, destinationValues, pixelCount: 2);

        Assert.True(destinationValues[0] > 200, $"Expected white RGB to convert to a bright gray value, got {destinationValues[0]}.");
        Assert.True(destinationValues[1] < 20, $"Expected black RGB to convert to a dark gray value, got {destinationValues[1]}.");
    }

    [Fact]
    public void ConvertTo_RgbToRgb_RoundTripsThroughIdenticalProfile()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());

        Span<byte> deviceValues = [12, 200, 90];
        Span<byte> destinationValues = stackalloc byte[3];
        source.ConvertTo(destination, deviceValues, destinationValues, pixelCount: 1);

        // Same profile on both ends, relative colorimetric-shaped (linear TRC + matrix) transform: converting
        // through PCS and back should reproduce the original device values almost exactly.
        Assert.InRange(destinationValues[0], deviceValues[0] - 1, deviceValues[0] + 1);
        Assert.InRange(destinationValues[1], deviceValues[1] - 1, deviceValues[1] + 1);
        Assert.InRange(destinationValues[2], deviceValues[2] - 1, deviceValues[2] + 1);
    }

    [Fact]
    public void ConvertTo_DestinationNull_Throws()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        Assert.Throws<ArgumentNullException>(() => source.ConvertTo(null!, [1, 2, 3], stackalloc byte[3], pixelCount: 1));
    }

    [Fact]
    public void ConvertTo_WrongDeviceValuesLength_Throws()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());
        Assert.Throws<ArgumentException>(() => source.ConvertTo(destination, new byte[2], new byte[1], pixelCount: 1));
    }

    [Fact]
    public void ConvertTo_WrongDestinationValuesLength_Throws()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());
        Assert.Throws<ArgumentException>(() => source.ConvertTo(destination, new byte[3], new byte[2], pixelCount: 1));
    }

    [Fact]
    public void ConvertTo_NegativePixelCount_Throws()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ConvertTo(destination, [], [], pixelCount: -1));
    }

    [Fact]
    public void ConvertTo_BlackPointCompensation_LiftsSourceBlackTowardDestinationBlack()
    {
        // Source declares a non-zero bkpt (its darkest achievable output isn't quite true black); destination
        // has no bkpt, so its black point is estimated from device black (0.0), which for a linear grey TRC
        // resolves to PCS (0,0,0) exactly -- true black. Converting a mid-gray value should land darker with
        // BPC enabled than without it, since BPC stretches the source's shadow range down toward true black.
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile(blackPointX: 0.04821, blackPointY: 0.05, blackPointZ: 0.041245));
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());

        Span<byte> deviceValue = [128];
        Span<byte> withoutBpc = stackalloc byte[1];
        Span<byte> withBpc = stackalloc byte[1];

        // Pinned to RelativeColorimetric explicitly: the profile's own default is Perceptual, whose PCS
        // adjustment (a separate, fixed black-point stretch unrelated to blackPointCompensation) would
        // otherwise compound with the one under test here.
        source.ConvertTo(destination, deviceValue, withoutBpc, pixelCount: 1, intent: IccRenderingIntent.RelativeColorimetric, blackPointCompensation: false);
        source.ConvertTo(destination, deviceValue, withBpc, pixelCount: 1, intent: IccRenderingIntent.RelativeColorimetric, blackPointCompensation: true);

        Assert.True(withBpc[0] < withoutBpc[0], $"Expected black point compensation to darken the result (without={withoutBpc[0]}, with={withBpc[0]}).");
    }

    [Fact]
    public void ConvertTo_BlackPointCompensation_IgnoredForAbsoluteColorimetric()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile(blackPointX: 0.04821, blackPointY: 0.05, blackPointZ: 0.041245));
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());

        Span<byte> deviceValue = [128];
        Span<byte> withoutBpc = stackalloc byte[1];
        Span<byte> withBpc = stackalloc byte[1];

        source.ConvertTo(destination, deviceValue, withoutBpc, pixelCount: 1, intent: IccRenderingIntent.AbsoluteColorimetric, blackPointCompensation: false);
        source.ConvertTo(destination, deviceValue, withBpc, pixelCount: 1, intent: IccRenderingIntent.AbsoluteColorimetric, blackPointCompensation: true);

        Assert.Equal(withoutBpc[0], withBpc[0]);
    }

    [Fact]
    public void ConvertToSrgb_AbsoluteColorimetric_NoMediaWhitePointTag_ThrowsNotSupportedExceptionNotNullReferenceException()
    {
        // A profile missing its (ICC.1:2010 §8.2-mandatory) wtpt tag is malformed, but AbsoluteColorimetric is
        // the only intent that ever reads it -- this should surface as a clear NotSupportedException the first
        // time it's actually needed, not a bare NullReferenceException deep in the engine.
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfileWithoutWhitePoint());

        var exception = Assert.Throws<NotSupportedException>(() =>
            profile.ConvertToSrgb([128], stackalloc byte[4], pixelCount: 1, intent: IccRenderingIntent.AbsoluteColorimetric));
        Assert.Contains("wtpt", exception.Message);
    }

    [Fact]
    public void ConvertTo_AbsoluteColorimetric_NoMediaWhitePointTag_ThrowsNotSupportedExceptionNotNullReferenceException()
    {
        var source = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfileWithoutWhitePoint());
        var destination = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());

        var exception = Assert.Throws<NotSupportedException>(() =>
            source.ConvertTo(destination, [128], stackalloc byte[1], pixelCount: 1, intent: IccRenderingIntent.AbsoluteColorimetric));
        Assert.Contains("wtpt", exception.Message);
    }

    [Fact]
    public void TryConvertToSrgb_Rgb_MatchesByteConvertToSrgbWithinOneLevel()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());
        byte[][] samples = [[255, 255, 255], [0, 0, 0], [200, 30, 90], [12, 128, 250]];

        foreach (var device in samples)
        {
            var expected = new byte[4];
            profile.ConvertToSrgb(device, expected, pixelCount: 1);

            float[] components = [device[0] / 255f, device[1] / 255f, device[2] / 255f];
            Assert.True(profile.TryConvertToSrgb(components, out float r, out float g, out float b));

            Assert.InRange(r * 255f, expected[0] - 0.51f, expected[0] + 0.51f);
            Assert.InRange(g * 255f, expected[1] - 0.51f, expected[1] + 0.51f);
            Assert.InRange(b * 255f, expected[2] - 0.51f, expected[2] + 0.51f);
        }
    }

    [Fact]
    public void TryConvertToSrgb_Rgb_IsNotQuantizedToEightBits()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());

        Assert.True(profile.TryConvertToSrgb([0.4f, 0.4f, 0.4f], out float r, out _, out _));

        Assert.NotEqual(MathF.Round(r * 255f), r * 255f, precision: 3);
    }

    [Fact]
    public void TryConvertToSrgb_Gray_WhiteAndBlack()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfile());

        Assert.True(profile.TryConvertToSrgb([1f], out float wr, out float wg, out float wb));
        Assert.True(wr > 0.95f && wg > 0.95f && wb > 0.95f);
        Assert.True(profile.TryConvertToSrgb([0f], out float kr, out float kg, out float kb));
        Assert.True(kr < 0.05f && kg < 0.05f && kb < 0.05f);
    }

    [Fact]
    public void TryConvertToSrgb_OutOfRangeComponents_AreClampedAndOutputStaysInRange()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());

        Assert.True(profile.TryConvertToSrgb([2f, -1f, 0.5f], out float r, out float g, out float b));

        Assert.InRange(r, 0f, 1f);
        Assert.InRange(g, 0f, 1f);
        Assert.InRange(b, 0f, 1f);
    }

    [Fact]
    public void TryConvertToSrgb_WrongComponentCount_ReturnsFalse()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());

        Assert.False(profile.TryConvertToSrgb([0.5f], out float r, out float g, out float b));
        Assert.Equal(0f, r + g + b);
        Assert.False(profile.TryConvertToSrgb([0.1f, 0.2f, 0.3f, 0.4f], out _, out _, out _));
    }

    [Fact]
    public void TryConvertToSrgb_NonFiniteComponent_ReturnsFalse()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());

        Assert.False(profile.TryConvertToSrgb([float.NaN, 0f, 0f], out _, out _, out _));
        Assert.False(profile.TryConvertToSrgb([float.PositiveInfinity, 0f, 0f], out _, out _, out _));
    }

    [Fact]
    public void TryConvertToSrgb_AbsoluteColorimetric_NoMediaWhitePointTag_ReturnsFalseInsteadOfThrowing()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildGrayTrcProfileWithoutWhitePoint());

        Assert.False(profile.TryConvertToSrgb([0.5f], out _, out _, out _, IccRenderingIntent.AbsoluteColorimetric));
    }

    [Fact]
    public void TryConvertToSrgb_BlackPointCompensation_ChangesShadowsForProfileWithRaisedBlack()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile(blackPointX: 0.02, blackPointY: 0.02, blackPointZ: 0.02));

        Assert.True(profile.TryConvertToSrgb([0f, 0f, 0f], out float without, out _, out _, IccRenderingIntent.RelativeColorimetric, blackPointCompensation: false));
        Assert.True(profile.TryConvertToSrgb([0f, 0f, 0f], out float with, out _, out _, IccRenderingIntent.RelativeColorimetric, blackPointCompensation: true));

        Assert.True(with <= without);
    }

    [Fact]
    public void Description_IsNull_WhenProfileHasNoDescTag()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile());

        Assert.Null(profile.Description);
    }

    [Fact]
    public void Description_ReadsVersion2TextDescriptionTag()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile(descriptionTag: SyntheticIccProfileBuilder.BuildTextDescriptionTag("Test RGB v2")));

        Assert.Equal("Test RGB v2", profile.Description);
    }

    [Fact]
    public void Description_ReadsVersion4MultiLocalizedUnicodeTag_PreferringEnglish()
    {
        var tag = SyntheticIccProfileBuilder.BuildMultiLocalizedUnicodeTag(("de", "Testprofil"), ("en", "Test RGB v4 é"));
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile(descriptionTag: tag));

        Assert.Equal("Test RGB v4 é", profile.Description);
    }

    [Fact]
    public void Description_FallsBackToFirstMultiLocalizedUnicodeRecord_WhenNoEnglish()
    {
        var tag = SyntheticIccProfileBuilder.BuildMultiLocalizedUnicodeTag(("de", "Testprofil"), ("fr", "Profil"));
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile(descriptionTag: tag));

        Assert.Equal("Testprofil", profile.Description);
    }

    [Fact]
    public void Description_IsNull_ForMalformedDescTag()
    {
        var profile = new IccColorProfile(SyntheticIccProfileBuilder.BuildRgbTrcMatrixProfile(descriptionTag: new byte[] { 1, 2, 3 }));

        Assert.Null(profile.Description);
    }
}
