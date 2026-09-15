using PeachImage.Internal.Icc;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage;

/// <summary>
/// A parsed ICC color profile, exposing PeachImage's optimized, colorimetric device→sRGB conversion to
/// library consumers directly — the same engine JPEG decode already uses automatically for a CMYK source with
/// an embedded profile (see <see cref="ImageMetadata.GetIccColorProfile"/> and <see cref="Image.ConvertToSrgb"/>
/// for the common case of converting an already-decoded <see cref="Image"/>). Supports Gray, RGB, and CMYK
/// device color spaces — whatever the profile itself declares via <see cref="DataColorSpace"/>.
/// </summary>
/// <remarks>
/// Immutable and safe to use concurrently from multiple threads once constructed: parsing happens once, in
/// the constructor, and <see cref="ConvertToSrgb"/> is pure computation over caller-supplied buffers with no
/// shared mutable state.
/// </remarks>
public sealed class IccColorProfile
{
    private readonly IccProfile profile;
    private readonly IccIntent defaultIntent;

    /// <summary>
    /// Parses <paramref name="profileBytes"/> as an ICC profile.
    /// </summary>
    /// <exception cref="IccProfileException">
    /// <paramref name="profileBytes"/> is too short, malformed, or resolves to a device color space or
    /// transform PeachImage's ICC engine doesn't support (see <see cref="DataColorSpace"/>'s
    /// <see cref="IccColorSpace.Unknown"/> case for what's supported). Use <see cref="TryCreate"/> instead if
    /// this is an expected, common outcome for your input rather than an error.
    /// </exception>
    public IccColorProfile(ReadOnlySpan<byte> profileBytes)
    {
        if (!IccDeviceToSrgbConverter.TryParse(profileBytes.ToArray(), out var parsedProfile, out var intent))
        {
            throw new IccProfileException("The ICC profile data is too short, malformed, or resolves to a device color space or transform that isn't supported.");
        }

        profile = parsedProfile;
        defaultIntent = intent;
        DataColorSpace = ToPublicColorSpace(profile.DataColorSpace);
    }

    private IccColorProfile(IccProfile profile, IccIntent defaultIntent)
    {
        this.profile = profile;
        this.defaultIntent = defaultIntent;
        DataColorSpace = ToPublicColorSpace(profile.DataColorSpace);
    }

    /// <summary>Attempts to parse <paramref name="profileBytes"/> as an ICC profile, returning <see langword="false"/> instead of throwing on any failure.</summary>
    public static bool TryCreate(ReadOnlySpan<byte> profileBytes, out IccColorProfile? profile)
    {
        if (IccDeviceToSrgbConverter.TryParse(profileBytes.ToArray(), out var parsedProfile, out var intent))
        {
            profile = new IccColorProfile(parsedProfile, intent);
            return true;
        }

        profile = null;
        return false;
    }

    /// <summary>The device color space this profile's transform expects.</summary>
    public IccColorSpace DataColorSpace { get; }

    /// <summary>The number of device channels <see cref="ConvertToSrgb"/> expects per pixel (1 for <see cref="IccColorSpace.Gray"/>, 3 for <see cref="IccColorSpace.Rgb"/>, 4 for <see cref="IccColorSpace.Cmyk"/>).</summary>
    public int ChannelCount => profile.ChannelCount;

    /// <summary>The profile's own declared default rendering intent, used by <see cref="ConvertToSrgb"/> when its <c>intent</c> parameter is <see langword="null"/>.</summary>
    public IccRenderingIntent DefaultRenderingIntent => ToPublicIntent(defaultIntent);

    /// <summary>
    /// Converts <paramref name="pixelCount"/> pixels of normalized device values to RGBA32 (alpha always 255),
    /// using this profile's real device→PCS transform — a colorimetric, optimized (SIMD-tailed, allocation-free)
    /// conversion, not a naive formula.
    /// </summary>
    /// <param name="deviceValues">
    /// <paramref name="pixelCount"/> * <see cref="ChannelCount"/> bytes: one byte per device channel per
    /// pixel, tightly interleaved (e.g. C,M,Y,K,C,M,Y,K,… for a <see cref="IccColorSpace.Cmyk"/> profile).
    /// </param>
    /// <param name="destination">
    /// <paramref name="pixelCount"/> * 4 bytes: the RGBA32 output, tightly interleaved.
    /// </param>
    /// <param name="pixelCount">The number of pixels to convert.</param>
    /// <param name="intent">The rendering intent to use, or <see langword="null"/> to use <see cref="DefaultRenderingIntent"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="deviceValues"/> or <paramref name="destination"/> isn't sized as documented above.</exception>
    public void ConvertToSrgb(ReadOnlySpan<byte> deviceValues, Span<byte> destination, int pixelCount, IccRenderingIntent? intent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixelCount);

        int expectedDeviceLength = pixelCount * ChannelCount;
        if (deviceValues.Length != expectedDeviceLength)
        {
            throw new ArgumentException($"Expected {expectedDeviceLength} device value bytes ({pixelCount} pixels x {ChannelCount} channels), but got {deviceValues.Length}.", nameof(deviceValues));
        }

        int expectedDestinationLength = pixelCount * 4;
        if (destination.Length != expectedDestinationLength)
        {
            throw new ArgumentException($"Expected {expectedDestinationLength} destination bytes ({pixelCount} pixels x 4 RGBA channels), but got {destination.Length}.", nameof(destination));
        }

        var resolvedIntent = intent is { } requestedIntent ? ToInternalIntent(requestedIntent) : defaultIntent;
        IccDeviceToSrgbConverter.Convert(deviceValues, destination, pixelCount, profile, resolvedIntent);
    }

    private static IccColorSpace ToPublicColorSpace(string dataColorSpace) => dataColorSpace switch
    {
        IccSignatures.Grey => IccColorSpace.Gray,
        IccSignatures.Rgb => IccColorSpace.Rgb,
        IccSignatures.Cmyk => IccColorSpace.Cmyk,
        _ => IccColorSpace.Unknown,
    };

    private static IccRenderingIntent ToPublicIntent(IccIntent intent) => intent switch
    {
        IccIntent.RelativeColorimetric => IccRenderingIntent.RelativeColorimetric,
        IccIntent.Saturation => IccRenderingIntent.Saturation,
        IccIntent.AbsoluteColorimetric => IccRenderingIntent.AbsoluteColorimetric,
        _ => IccRenderingIntent.Perceptual,
    };

    private static IccIntent ToInternalIntent(IccRenderingIntent intent) => intent switch
    {
        IccRenderingIntent.Perceptual => IccIntent.Perceptual,
        IccRenderingIntent.RelativeColorimetric => IccIntent.RelativeColorimetric,
        IccRenderingIntent.Saturation => IccIntent.Saturation,
        IccRenderingIntent.AbsoluteColorimetric => IccIntent.AbsoluteColorimetric,
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, message: null),
    };
}
