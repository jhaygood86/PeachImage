using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Maps a real pixel distance to the "plane code" <see cref="Decoding.Vp8L.Vp8LBackwardReferenceTables.PlaneCodeToDistance"/>
/// would decode back into it — the mirror of that function, built by inverting it rather than re-deriving
/// the 120-entry neighborhood table's geometry independently.
/// </summary>
internal sealed class Vp8LDistanceMapper
{
    private const int CodeToPlaneCodes = 120;

    private readonly Dictionary<int, int> _distanceToPlaneCode;

    public Vp8LDistanceMapper(int width)
    {
        _distanceToPlaneCode = new Dictionary<int, int>(CodeToPlaneCodes);

        // Walk every short neighborhood code and keep the smallest plane code for each distance it can
        // produce -- a smaller plane code needs fewer (or no) extra bits once run through
        // Vp8LPrefixCodeEncoder, so it's always at least as cheap as a larger colliding code.
        for (int planeCode = CodeToPlaneCodes; planeCode >= 1; planeCode--)
        {
            int distance = Vp8LBackwardReferenceTables.PlaneCodeToDistance(width, planeCode);
            _distanceToPlaneCode[distance] = planeCode;
        }
    }

    /// <summary>Returns the plane code for <paramref name="distance"/>: a short neighborhood code when one maps to this exact distance, otherwise the raw large-distance fallback.</summary>
    public int DistanceToPlaneCode(int distance) =>
        _distanceToPlaneCode.TryGetValue(distance, out int planeCode) ? planeCode : distance + CodeToPlaneCodes;
}
