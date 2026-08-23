namespace PeachImage;

/// <summary>
/// Which part of the source survives cropping (<see cref="ResizeMode.Crop"/>), or where the source is
/// placed within the padded canvas (<see cref="ResizeMode.Pad"/>). Ignored for <see cref="ResizeMode.Exact"/>
/// and <see cref="ResizeMode.Max"/>.
/// </summary>
public enum AnchorPosition
{
    /// <summary>The top-left corner.</summary>
    TopLeft,

    /// <summary>The top edge, horizontally centered.</summary>
    TopCenter,

    /// <summary>The top-right corner.</summary>
    TopRight,

    /// <summary>The left edge, vertically centered.</summary>
    MiddleLeft,

    /// <summary>The center. The default.</summary>
    MiddleCenter,

    /// <summary>The right edge, vertically centered.</summary>
    MiddleRight,

    /// <summary>The bottom-left corner.</summary>
    BottomLeft,

    /// <summary>The bottom edge, horizontally centered.</summary>
    BottomCenter,

    /// <summary>The bottom-right corner.</summary>
    BottomRight,
}
