namespace PeachImage.Internal.Icc;

/// <summary>
/// Shared lower/upper-index lookup for 1D table interpolation, used by both <see cref="IccCurve"/> (a table
/// curve's sample array) and <see cref="IccClut"/> (per-dimension grid indexing). Ported from
/// Wacton/Unicolour's <c>Lut.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal static class IccLut
{
    internal static (int LowerIndex, int UpperIndex, double Distance) Lookup(int valueCount, double normalizedValue)
    {
        double clamped = Math.Clamp(normalizedValue, 0.0, 1.0);
        double exactIndex = clamped * (valueCount - 1);
        int lowerIndex = (int)Math.Floor(exactIndex);
        int upperIndex = (int)Math.Ceiling(exactIndex);
        double distance = exactIndex - lowerIndex;
        return (lowerIndex, upperIndex, distance);
    }
}
