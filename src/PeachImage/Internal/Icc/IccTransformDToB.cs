namespace PeachImage.Internal.Icc;

/// <summary>
/// The D-to-B/B-to-D transform (ICC.1:2010 v4+'s newer, more general LUT tags) — not supported by this port
/// (nor by Wacton/Unicolour, which this is ported from — MIT license, see THIRD-PARTY-LICENSES.md); a profile
/// resolving to this transform falls back to the naive CMYK→RGB conversion at the call site, the same as any
/// other unsupported/unparseable profile.
/// </summary>
internal sealed class IccTransformDToB : IccTransform
{
    internal IccTransformDToB(IccHeader header, IccTags tags)
        : base(header, tags, hasPerceptualHandling: true)
    {
    }

    internal override IccVector3 ToXyz(ReadOnlySpan<double> deviceValues, IccIntent intent) =>
        throw new NotSupportedException("D-to-B ICC transforms are not supported.");

    internal override void FromXyz(IccVector3 xyz, IccIntent intent, Span<double> deviceValues) =>
        throw new NotSupportedException("B-to-D ICC transforms are not supported.");
}
