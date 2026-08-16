namespace PeachImage;

/// <summary>
/// How an <see cref="AnimatedImageFrame"/>'s canvas region should be treated before the next frame is
/// drawn. Modeled on GIF's Graphic Control Extension disposal method (values 0-3; 0 "unspecified" and 1
/// "do not dispose" both mean "leave as-is" in practice, so they share <see cref="DoNotDispose"/>/
/// <see cref="None"/> here for the raw distinction while behaving identically) — the only format that
/// uses it today, but the concept (and this type) is codec-agnostic.
/// </summary>
public enum FrameDisposalMethod
{
    /// <summary>No disposal specified. Treated the same as <see cref="DoNotDispose"/>.</summary>
    None,

    /// <summary>Leave the frame's pixels in place for subsequent frames.</summary>
    DoNotDispose,

    /// <summary>Clear the frame's region to the background (transparent) before the next frame is drawn.</summary>
    RestoreToBackground,

    /// <summary>Restore the frame's region to what it looked like before this frame was drawn.</summary>
    RestoreToPrevious,
}
