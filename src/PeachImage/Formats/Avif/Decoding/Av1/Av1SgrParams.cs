namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary><c>Sgr_Params</c> (spec §7.17.3), extracted directly from the specification text. Each row is <c>[r0, eps0, r1, eps1]</c> for the self-guided restoration's two box-filter passes.</summary>
internal static class Av1SgrParams
{
    public static readonly int[][] Table =
    [
        [2, 12, 1, 4],
        [2, 15, 1, 6],
        [2, 18, 1, 8],
        [2, 21, 1, 9],
        [2, 24, 1, 10],
        [2, 29, 1, 11],
        [2, 36, 1, 12],
        [2, 45, 1, 13],
        [2, 56, 1, 14],
        [2, 68, 1, 15],
        [0, 0, 1, 5],
        [0, 0, 1, 8],
        [0, 0, 1, 11],
        [0, 0, 1, 14],
        [2, 30, 0, 0],
        [2, 75, 0, 0],
    ];
}
