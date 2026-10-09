namespace PeachImage.Formats.Avif.Container;

/// <summary>One transformative item property: an <c>irot</c> rotation or an <c>imir</c> mirror.</summary>
/// <param name="IsMirror"><see langword="true"/> for <c>imir</c>, <see langword="false"/> for <c>irot</c>.</param>
/// <param name="Value">For <c>irot</c>, the angle in units of 90 degrees anti-clockwise (0 to 3). For <c>imir</c>, the axis (0 or 1).</param>
internal readonly record struct AvifTransform(bool IsMirror, int Value);

/// <summary>
/// Parses the <c>irot</c>/<c>imir</c> item properties and folds an ordered list of them into the equivalent EXIF orientation.
/// Interpretation used (ISO/IEC 23008-12 as amended, the form the AVIF and MIAF specs reference):
/// <list type="bullet">
/// <item><c>irot</c>: the image is rotated anti-clockwise by <c>angle * 90</c> degrees.</item>
/// <item><c>imir</c>: axis 0 mirrors about a vertical axis (left and right swap); axis 1 mirrors about a horizontal axis (top and bottom swap).</item>
/// <item>Transformative properties are applied in the order they are associated with the item (<c>ipma</c> order), so rotate-then-mirror differs from mirror-then-rotate.</item>
/// <item>irot/imir take precedence over any Exif orientation (MIAF), so only they are consulted; no transform means <see cref="ImageOrientation.Normal"/>.</item>
/// </list>
/// </summary>
internal static class AvifTransformProperties
{
    /// <summary>Parses an <c>irot</c> box: a plain Box whose single byte holds the angle in its low 2 bits. Returns <see langword="null"/> if truncated.</summary>
    public static AvifTransform? ParseIrot(byte[] data, AvifBox box) =>
        box.PayloadLength < 1 ? null : new AvifTransform(IsMirror: false, data[box.PayloadOffset] & 0x3);

    /// <summary>Parses an <c>imir</c> box: a plain Box whose single byte holds the axis in its low bit. Returns <see langword="null"/> if truncated.</summary>
    public static AvifTransform? ParseImir(byte[] data, AvifBox box) =>
        box.PayloadLength < 1 ? null : new AvifTransform(IsMirror: true, data[box.PayloadOffset] & 0x1);

    /// <summary>Maps the transforms (in application order) to the EXIF orientation that displays the stored image the same way.</summary>
    public static ImageOrientation ToOrientation(IEnumerable<AvifTransform> transforms)
    {
        // Track the combined transform as a 2x2 integer matrix acting on (x, y) with x right and y down:
        // x' = a*x + b*y, y' = c*x + d*y. Start at identity; each op is applied after the previous ones.
        int a = 1, b = 0, c = 0, d = 1;
        foreach (var t in transforms)
        {
            (int ma, int mb, int mc, int md) = t.IsMirror
                ? (t.Value == 0 ? (-1, 0, 0, 1) : (1, 0, 0, -1))
                : t.Value switch
                {
                    1 => (0, 1, -1, 0), // 90 degrees anti-clockwise: right -> up
                    2 => (-1, 0, 0, -1),
                    3 => (0, -1, 1, 0), // 270 anti-clockwise = 90 clockwise: right -> down
                    _ => (1, 0, 0, 1),
                };

            (a, b, c, d) = ((ma * a) + (mb * c), (ma * b) + (mb * d), (mc * a) + (md * c), (mc * b) + (md * d));
        }

        // EXIF orientations expressed as the same matrices (the transform that turns the stored image into the displayed one).
        return (a, b, c, d) switch
        {
            (-1, 0, 0, 1) => ImageOrientation.MirrorHorizontal,
            (-1, 0, 0, -1) => ImageOrientation.Rotate180,
            (1, 0, 0, -1) => ImageOrientation.MirrorVertical,
            (0, 1, 1, 0) => ImageOrientation.Transpose,
            (0, -1, 1, 0) => ImageOrientation.Rotate90,
            (0, -1, -1, 0) => ImageOrientation.Transverse,
            (0, 1, -1, 0) => ImageOrientation.Rotate270,
            _ => ImageOrientation.Normal,
        };
    }
}
