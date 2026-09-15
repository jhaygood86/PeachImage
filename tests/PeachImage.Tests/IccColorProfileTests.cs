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
}
