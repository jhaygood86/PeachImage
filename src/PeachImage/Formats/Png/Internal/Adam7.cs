namespace PeachImage.Formats.Png.Internal;

/// <summary>One of Adam7's 7 interlace passes: which pixels of the full image it covers.</summary>
internal readonly record struct Adam7Pass(int StartX, int StartY, int StrideX, int StrideY);

/// <summary>
/// Adam7 interlace pass geometry (spec §8.2). Shared verbatim between decode and encode since the
/// math is pure spec-mandated geometry, identical in both directions.
/// </summary>
internal static class Adam7
{
    private static readonly Adam7Pass[] PassTable =
    [
        new(0, 0, 8, 8),
        new(4, 0, 8, 8),
        new(0, 4, 4, 8),
        new(2, 0, 4, 4),
        new(0, 2, 2, 4),
        new(1, 0, 2, 2),
        new(0, 1, 1, 2),
    ];

    public static ReadOnlySpan<Adam7Pass> Passes => PassTable;

    /// <summary>The pixel dimensions of the reduced sub-image for <paramref name="pass"/>, given the full image's dimensions. Either component is 0 when the pass contributes no pixels.</summary>
    public static (int Width, int Height) GetPassDimensions(int fullWidth, int fullHeight, in Adam7Pass pass)
    {
        int width = fullWidth > pass.StartX ? ((fullWidth - pass.StartX + pass.StrideX - 1) / pass.StrideX) : 0;
        int height = fullHeight > pass.StartY ? ((fullHeight - pass.StartY + pass.StrideY - 1) / pass.StrideY) : 0;
        return (width, height);
    }
}
