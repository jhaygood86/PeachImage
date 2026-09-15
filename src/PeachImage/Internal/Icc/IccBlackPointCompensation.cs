namespace PeachImage.Internal.Icc;

/// <summary>
/// Black point compensation (ICC.1:2010 Annex A, informative): linearly rescales a source profile's PCS
/// output so its black point maps to a destination profile's black point instead of clipping/crushing
/// against it, anchored at the PCS's own fixed D50 white point (<see cref="IccColorMath.IccPcsWhite"/>) --
/// both profiles' Perceptual/RelativeColorimetric/Saturation transforms already normalize to that shared
/// white internally, so only black needs remapping here. Not meaningful for
/// <see cref="IccIntent.AbsoluteColorimetric"/>, which by definition preserves absolute media black rather
/// than compensating for it (see <see cref="AppliesTo"/>).
/// </summary>
internal static class IccBlackPointCompensation
{
    /// <summary>Whether black point compensation applies to <paramref name="intent"/> at all.</summary>
    internal static bool AppliesTo(IccIntent intent) => intent != IccIntent.AbsoluteColorimetric;

    /// <summary>
    /// Rescales <paramref name="xyz"/> (a source profile's PCS output) so <paramref name="sourceBlack"/> maps
    /// to <paramref name="destinationBlack"/>, per axis, while the PCS white point stays fixed.
    /// </summary>
    internal static IccVector3 Apply(IccVector3 xyz, IccVector3 sourceBlack, IccVector3 destinationBlack)
    {
        var white = IccColorMath.IccPcsWhite;
        return new IccVector3(
            Scale(xyz.X, sourceBlack.X, destinationBlack.X, white.X),
            Scale(xyz.Y, sourceBlack.Y, destinationBlack.Y, white.Y),
            Scale(xyz.Z, sourceBlack.Z, destinationBlack.Z, white.Z));
    }

    private static double Scale(double value, double sourceBlack, double destinationBlack, double white)
    {
        // Clamp each black point to a physically sane range before using it as a scaling anchor: never
        // negative (light can't be produced below "no light"), and never beyond most of the way to white.
        // A declared bkpt tag or an out-of-gamut CLUT extrapolation (full ink coverage on every CMYK channel
        // can land slightly outside the profile's characterized gamut) could otherwise report a "black" at or
        // past white on some axis, which would flip the sign of -- or blow up -- the scale below.
        double clampedSourceBlack = Math.Clamp(sourceBlack, 0, white * 0.9);
        double clampedDestinationBlack = Math.Clamp(destinationBlack, 0, white * 0.9);

        double sourceRange = white - clampedSourceBlack;
        double scale = (white - clampedDestinationBlack) / sourceRange;
        double offset = clampedDestinationBlack - (clampedSourceBlack * scale);
        return (value * scale) + offset;
    }
}
