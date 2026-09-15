namespace PeachImage.Internal.Icc;

/// <summary>
/// A parsed ICC profile: header, tag table, and the device↔PCS transform selected from them. Adapted from
/// Wacton/Unicolour's <c>Icc/Profile.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md. Unlike the upstream
/// reference (a general color-management library that lets a caller chromatically adapt the profile's D50
/// PCS to any target white point via an injected, pluggable <c>ChromaticAdaptor</c>), this only ever needs to
/// reach one fixed destination — sRGB, whose reference white is D65 — so <see cref="ToXyzD50"/>/
/// <see cref="FromXyzD50"/> deliberately stay in the profile's own D50 PCS; the caller (the CMYK→RGBA32
/// conversion kernel) applies the fixed Bradford D50→D65-then-linear-sRGB matrix from
/// <see cref="IccColorMath"/> itself, once, rather than this type taking a pluggable adaptor it would only
/// ever be given one fixed value for.
/// </summary>
internal sealed class IccProfile
{
    private readonly IccHeader header;
    private readonly IccTags tags;
    private readonly IccTransform transform;

    internal IccProfile(byte[] bytes)
    {
        if (bytes.Length < 128)
        {
            throw new ArgumentException("ICC profile data does not contain enough bytes for a valid 128-byte header.", nameof(bytes));
        }

        using var stream = new MemoryStream(bytes, writable: false);
        header = new IccHeader(stream);
        tags = new IccTags(stream);
        transform = SelectTransform();
    }

    /// <summary>The device color space the profile's transform expects (e.g. <see cref="IccSignatures.Cmyk"/>).</summary>
    internal string DataColorSpace => header.DataColorSpace;

    /// <summary>The profile's own declared default rendering intent, used when a caller doesn't specify one.</summary>
    internal IccIntent DefaultIntent => header.Intent;

    /// <summary>
    /// The number of device channels <see cref="DataColorSpace"/> implies (Grey=1, Rgb=3, Cmyk=4 — a fixed,
    /// spec-defined relationship). 0 for any other device color space, which <see cref="ErrorIfUnsupported"/>
    /// already rejects, so callers that check that first never observe 0 here.
    /// </summary>
    internal int ChannelCount => DataColorSpace switch
    {
        IccSignatures.Grey => 1,
        IccSignatures.Rgb => 3,
        IccSignatures.Cmyk => 4,
        _ => 0,
    };

    private bool IsTransformSupported => transform is IccTransformAToB or IccTransformTrcMatrix or IccTransformTrcGrey;

    /// <summary>Throws if this profile isn't one PeachImage's ICC engine can use, so callers can fall back to a naive conversion instead.</summary>
    internal void ErrorIfUnsupported()
    {
        if (header.ProfileFileSignature != IccSignatures.Profile)
        {
            throw new NotSupportedException($"ICC profile signature is incorrect: expected '{IccSignatures.Profile}' but was '{header.ProfileFileSignature}'.");
        }

        if (header.ProfileClass is not (IccSignatures.Input or IccSignatures.Display or IccSignatures.Output or IccSignatures.ColorSpace))
        {
            throw new NotSupportedException($"ICC profile class '{header.ProfileClass}' is not supported.");
        }

        if (!IsTransformSupported)
        {
            throw new NotSupportedException($"ICC transform '{transform.GetType().Name}' is not supported.");
        }

        if (ChannelCount == 0)
        {
            throw new NotSupportedException($"ICC device color space '{header.DataColorSpace}' is not supported.");
        }
    }

    /// <summary>Converts normalized (0-1) device values (e.g. 4 CMYK channels) to the profile's own D50 profile connection space.</summary>
    internal IccVector3 ToXyzD50(ReadOnlySpan<double> deviceValues, IccIntent intent) => transform.ToXyz(deviceValues, intent);

    /// <summary>
    /// Converts a D50 profile-connection-space value back to normalized (0-1) device values. Used both for
    /// round-trip verification and, as the sink stage of <see cref="IccColorProfile.ConvertTo"/>, as an actual
    /// per-pixel conversion kernel when this profile is a conversion's destination.
    /// </summary>
    internal void FromXyzD50(IccVector3 xyzD50, IccIntent intent, Span<double> deviceValues) => transform.FromXyz(xyzD50, intent, deviceValues);

    /// <summary>
    /// This profile's black point in D50 PCS XYZ, in the same (possibly perceptually-adjusted) PCS space
    /// <see cref="ToXyzD50"/> itself produces under <paramref name="intent"/> -- for black point compensation
    /// (<see cref="IccBlackPointCompensation"/>, ICC.1:2010 Annex A). Prefers the profile's own <c>bkpt</c> tag
    /// when present (a CMM-consumed hint some real profiles declare specifically for this purpose), adjusted
    /// the same way a device→PCS conversion would be since the tag itself is stored as an unadjusted
    /// colorimetric PCS value; otherwise estimates it by running the profile's own darkest achievable device
    /// value through its forward transform under <paramref name="intent"/> -- all-zero for an additive
    /// Gray/RGB device (0 = no light), all-one for a subtractive CMYK device (full ink coverage on every
    /// channel, the darkest a real press can produce).
    /// </summary>
    internal IccVector3 GetBlackPointXyzD50(IccIntent intent)
    {
        if (tags.MediaBlack.Value is { } bkpt)
        {
            return transform.AdjustBlackPointXyz(new IccVector3(bkpt.X, bkpt.Y, bkpt.Z), intent);
        }

        Span<double> deviceBlack = stackalloc double[ChannelCount];
        if (DataColorSpace == IccSignatures.Cmyk)
        {
            deviceBlack.Fill(1.0);
        }

        return transform.ToXyz(deviceBlack, intent);
    }

    /*
     * Transform tag precedence for input/display/output/color-space profile types (ICC.1:2010 Annex B):
     * 1) The BToD and DToB tag families, if present (v5+/iccMAX device-link tags -- not supported here, see
     *    IccTransformDToB).
     * 2) The BToA and AToB tag families, if present, when 1) isn't used.
     * 3) BToA0/AToB0 specifically, when 1) and 2) aren't used.
     * 4) The RGB-matrix-TRC or grey-TRC tags, when none of the above are used.
     */
    private IccTransform SelectTransform()
    {
        if (tags.HasAny(IccSignatures.DToB0, IccSignatures.DToB1, IccSignatures.DToB2, IccSignatures.DToB3))
        {
            return new IccTransformDToB(header, tags);
        }

        if (tags.Has(IccSignatures.AToB0))
        {
            return new IccTransformAToB(header, tags);
        }

        if (tags.HasAll(IccSignatures.RedTrc, IccSignatures.GreenTrc, IccSignatures.BlueTrc, IccSignatures.RedMatrixColumn, IccSignatures.GreenMatrixColumn, IccSignatures.BlueMatrixColumn))
        {
            return new IccTransformTrcMatrix(header, tags);
        }

        if (tags.Has(IccSignatures.GreyTrc))
        {
            return new IccTransformTrcGrey(header, tags);
        }

        return new IccTransformNone(header, tags);
    }
}
