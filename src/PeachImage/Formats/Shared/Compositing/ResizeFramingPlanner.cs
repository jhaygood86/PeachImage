using PeachImage.Formats.Shared.Resampling;

namespace PeachImage.Formats.Shared.Compositing;

/// <summary>
/// The intermediate size to resize to, and the offset to place/crop at, for one <see cref="ResizeMode.Crop"/>
/// or <see cref="ResizeMode.Pad"/> resize.
/// </summary>
internal readonly record struct FramingPlan(int IntermediateWidth, int IntermediateHeight, int OffsetX, int OffsetY);

/// <summary>
/// Computes a <see cref="FramingPlan"/> for <see cref="ResizeMode.Crop"/>/<see cref="ResizeMode.Pad"/> —
/// shared by <see cref="Image.Resize(int, int, ResizeOptions?)"/> and
/// <see cref="AnimatedImage.Resize(int, int, ResizeOptions?)"/> so both apply identical scale-then-frame
/// math and anchor placement, the same sharing pattern <see cref="ResizeToFitCalculator"/> already
/// established for <see cref="ResizeMode.Max"/>.
/// </summary>
internal static class ResizeFramingPlanner
{
    /// <summary>Caller must only invoke this for <see cref="ResizeMode.Crop"/> or <see cref="ResizeMode.Pad"/>.</summary>
    public static FramingPlan Plan(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, ResizeMode mode, AnchorPosition anchor)
    {
        var (intermediateWidth, intermediateHeight) = mode == ResizeMode.Crop
            ? ResizeToFitCalculator.ComputeFillDimensions(sourceWidth, sourceHeight, targetWidth, targetHeight)
            : ResizeToFitCalculator.ComputeUnrestrictedFitDimensions(sourceWidth, sourceHeight, targetWidth, targetHeight);

        int horizontalSlack = mode == ResizeMode.Crop ? intermediateWidth - targetWidth : targetWidth - intermediateWidth;
        int verticalSlack = mode == ResizeMode.Crop ? intermediateHeight - targetHeight : targetHeight - intermediateHeight;

        return new FramingPlan(
            intermediateWidth,
            intermediateHeight,
            HorizontalOffset(anchor, horizontalSlack),
            VerticalOffset(anchor, verticalSlack));
    }

    // Center uses floor division for the odd-remainder case — deterministic, favors the top/left edge by
    // one pixel when the slack is odd. No existing convention to match; this is a fresh, arbitrary choice.
    private static int HorizontalOffset(AnchorPosition anchor, int slack) => anchor switch
    {
        AnchorPosition.TopLeft or AnchorPosition.MiddleLeft or AnchorPosition.BottomLeft => 0,
        AnchorPosition.TopRight or AnchorPosition.MiddleRight or AnchorPosition.BottomRight => slack,
        AnchorPosition.TopCenter or AnchorPosition.MiddleCenter or AnchorPosition.BottomCenter => slack / 2,
        _ => throw new ArgumentOutOfRangeException(nameof(anchor), anchor, message: null),
    };

    private static int VerticalOffset(AnchorPosition anchor, int slack) => anchor switch
    {
        AnchorPosition.TopLeft or AnchorPosition.TopCenter or AnchorPosition.TopRight => 0,
        AnchorPosition.BottomLeft or AnchorPosition.BottomCenter or AnchorPosition.BottomRight => slack,
        AnchorPosition.MiddleLeft or AnchorPosition.MiddleCenter or AnchorPosition.MiddleRight => slack / 2,
        _ => throw new ArgumentOutOfRangeException(nameof(anchor), anchor, message: null),
    };
}
