namespace PeachImage.Formats.Jpeg.Encoding;

/// <summary>Describes one progressive scan: which components it covers (interleaved if more than one)
/// and its spectral-selection/successive-approximation parameters.</summary>
internal readonly record struct ScanDescriptor(int[] ComponentIndices, int Ss, int Se, int Ah, int Al);

/// <summary>
/// Builds the fixed v1 default progressive scan script (spectral bands and successive-approximation
/// steps), adapted from libjpeg-turbo's default progression (jpeg_simple_progression) to this encoder's
/// component indexing (0=Y, 1=Cb, 2=Cr). Any spec-valid, monotonically-refining Ah/Al progression per
/// (component, band) decodes correctly — this exact split only affects how much benefit a partial
/// download gets, not correctness.
/// </summary>
internal static class ProgressiveScanScript
{
    private static readonly int[] Y = [0];
    private static readonly int[] Cb = [1];
    private static readonly int[] Cr = [2];
    private static readonly int[] Yuv = [0, 1, 2];

    private static readonly ScanDescriptor[] ColorScript =
    [
        new(Yuv, Ss: 0, Se: 0, Ah: 0, Al: 1),   // DC first (interleaved)
        new(Y, Ss: 1, Se: 5, Ah: 0, Al: 2),     // Y AC first, low band
        new(Cr, Ss: 1, Se: 63, Ah: 0, Al: 1),   // Cr AC first
        new(Cb, Ss: 1, Se: 63, Ah: 0, Al: 1),   // Cb AC first
        new(Y, Ss: 6, Se: 63, Ah: 0, Al: 2),    // Y AC first, remaining band
        new(Y, Ss: 1, Se: 63, Ah: 2, Al: 1),    // Y AC refine 2->1
        new(Yuv, Ss: 0, Se: 0, Ah: 1, Al: 0),   // DC refine (interleaved)
        new(Cr, Ss: 1, Se: 63, Ah: 1, Al: 0),   // Cr AC refine 1->0
        new(Cb, Ss: 1, Se: 63, Ah: 1, Al: 0),   // Cb AC refine 1->0
        new(Y, Ss: 1, Se: 63, Ah: 1, Al: 0),    // Y AC refine 1->0, final (usually largest)
    ];

    private static readonly ScanDescriptor[] GrayscaleScript =
    [
        new(Y, Ss: 0, Se: 0, Ah: 0, Al: 1),
        new(Y, Ss: 1, Se: 5, Ah: 0, Al: 2),
        new(Y, Ss: 6, Se: 63, Ah: 0, Al: 2),
        new(Y, Ss: 1, Se: 63, Ah: 2, Al: 1),
        new(Y, Ss: 0, Se: 0, Ah: 1, Al: 0),
        new(Y, Ss: 1, Se: 63, Ah: 1, Al: 0),
    ];

    public static IReadOnlyList<ScanDescriptor> BuildDefaultScript(int componentCount) => componentCount switch
    {
        1 => GrayscaleScript,
        3 => ColorScript,
        _ => throw new InvalidOperationException("Unreachable: only 1- or 3-component progressive frames are produced by this encoder."),
    };
}
