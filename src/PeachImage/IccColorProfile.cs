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
    /// <param name="blackPointCompensation">
    /// Whether to scale this profile's black point to sRGB's own black point (ICC.1:2010 Annex A), reducing
    /// shadow clipping/crushing for a source profile whose darkest achievable device value isn't quite true
    /// black. Meaningful for <see cref="IccRenderingIntent.Perceptual"/>, <see cref="IccRenderingIntent.RelativeColorimetric"/>,
    /// and <see cref="IccRenderingIntent.Saturation"/>; ignored for <see cref="IccRenderingIntent.AbsoluteColorimetric"/>,
    /// which preserves absolute media black by definition. Under <see cref="IccRenderingIntent.Perceptual"/>
    /// specifically, this compounds with that intent's own built-in shadow handling (distinct from this flag,
    /// and always applied regardless of it) -- if shadows look over-compressed with both in play, try
    /// <see cref="IccRenderingIntent.RelativeColorimetric"/> instead.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="deviceValues"/> or <paramref name="destination"/> isn't sized as documented above.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="intent"/> resolves to <see cref="IccRenderingIntent.AbsoluteColorimetric"/> and this
    /// profile has no media white point (<c>wtpt</c>) tag, which that intent requires.
    /// </exception>
    public void ConvertToSrgb(ReadOnlySpan<byte> deviceValues, Span<byte> destination, int pixelCount, IccRenderingIntent? intent = null, bool blackPointCompensation = false)
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
        IccDeviceToSrgbConverter.Convert(deviceValues, destination, pixelCount, profile, resolvedIntent, blackPointCompensation);
    }

    /// <summary>
    /// Converts <paramref name="pixelCount"/> pixels of normalized device values from this profile's device
    /// space to <paramref name="destination"/>'s device space, routing both profiles' device↔PCS transforms
    /// through the shared D50 profile connection space they each resolve to internally -- so this is the same
    /// operation whether <paramref name="destination"/> is a CMYK output-intent profile (converting
    /// RGB-authored content into it) or another device space entirely (e.g. one CMYK profile into another).
    /// <see cref="ConvertToSrgb"/> is this same conversion against a fixed, built-in sRGB destination,
    /// independently optimized since sRGB's device→PCS direction never varies.
    /// </summary>
    /// <param name="destination">The destination ICC profile.</param>
    /// <param name="deviceValues">
    /// <paramref name="pixelCount"/> * <see cref="ChannelCount"/> bytes: this profile's own device values,
    /// tightly interleaved (see <see cref="ConvertToSrgb"/>'s remarks on channel order).
    /// </param>
    /// <param name="destinationValues">
    /// <paramref name="pixelCount"/> * <paramref name="destination"/>'s own <see cref="ChannelCount"/> bytes:
    /// the converted device values in <paramref name="destination"/>'s color space, tightly interleaved.
    /// </param>
    /// <param name="pixelCount">The number of pixels to convert.</param>
    /// <param name="intent">
    /// The rendering intent to use for both this profile's forward transform and <paramref name="destination"/>'s
    /// reverse transform, or <see langword="null"/> to use this profile's own <see cref="DefaultRenderingIntent"/>.
    /// </param>
    /// <param name="blackPointCompensation">
    /// Whether to scale this profile's black point to <paramref name="destination"/>'s black point
    /// (ICC.1:2010 Annex A), reducing shadow clipping/crushing when the two profiles' darkest achievable
    /// device values differ. Meaningful for <see cref="IccRenderingIntent.Perceptual"/>,
    /// <see cref="IccRenderingIntent.RelativeColorimetric"/>, and <see cref="IccRenderingIntent.Saturation"/>;
    /// ignored for <see cref="IccRenderingIntent.AbsoluteColorimetric"/>, which preserves absolute media black
    /// by definition. Under <see cref="IccRenderingIntent.Perceptual"/> specifically, this compounds with that
    /// intent's own built-in shadow handling (distinct from this flag, and always applied regardless of it) --
    /// if shadows look over-compressed with both in play, try <see cref="IccRenderingIntent.RelativeColorimetric"/> instead.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="deviceValues"/> or <paramref name="destinationValues"/> isn't sized as documented above.</exception>
    /// <exception cref="NotSupportedException">
    /// Either <paramref name="destination"/> has no reverse (PCS→device) transform to convert into -- e.g. an
    /// AToB-only profile (such as a scanner "input" profile) with no corresponding BToA tag -- or
    /// <paramref name="intent"/> resolves to <see cref="IccRenderingIntent.AbsoluteColorimetric"/> and either
    /// profile has no media white point (<c>wtpt</c>) tag, which that intent requires.
    /// </exception>
    public void ConvertTo(IccColorProfile destination, ReadOnlySpan<byte> deviceValues, Span<byte> destinationValues, int pixelCount, IccRenderingIntent? intent = null, bool blackPointCompensation = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(pixelCount);

        int expectedDeviceLength = pixelCount * ChannelCount;
        if (deviceValues.Length != expectedDeviceLength)
        {
            throw new ArgumentException($"Expected {expectedDeviceLength} device value bytes ({pixelCount} pixels x {ChannelCount} channels), but got {deviceValues.Length}.", nameof(deviceValues));
        }

        int expectedDestinationLength = pixelCount * destination.ChannelCount;
        if (destinationValues.Length != expectedDestinationLength)
        {
            throw new ArgumentException($"Expected {expectedDestinationLength} destination value bytes ({pixelCount} pixels x {destination.ChannelCount} channels), but got {destinationValues.Length}.", nameof(destinationValues));
        }

        var resolvedIntent = intent is { } requestedIntent ? ToInternalIntent(requestedIntent) : defaultIntent;
        IccDeviceToDeviceConverter.Convert(deviceValues, destinationValues, pixelCount, profile, destination.profile, resolvedIntent, blackPointCompensation);
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
