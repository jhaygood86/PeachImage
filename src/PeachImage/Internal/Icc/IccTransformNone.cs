namespace PeachImage.Internal.Icc;

/// <summary>
/// The fallback when a profile has none of the tag combinations any other <see cref="IccTransform"/> needs.
/// Ported from Wacton/Unicolour's <c>Icc/TransformNone.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal sealed class IccTransformNone : IccTransform
{
    internal IccTransformNone(IccHeader header, IccTags tags)
        : base(header, tags, hasPerceptualHandling: true)
    {
    }

    internal override IccVector3 ToXyz(ReadOnlySpan<double> deviceValues, IccIntent intent) =>
        throw new NotSupportedException("This ICC profile has no usable device<->PCS transform tags.");

    internal override void FromXyz(IccVector3 xyz, IccIntent intent, Span<double> deviceValues) =>
        throw new NotSupportedException("This ICC profile has no usable device<->PCS transform tags.");
}
