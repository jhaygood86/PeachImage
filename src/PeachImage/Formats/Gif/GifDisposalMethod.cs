namespace PeachImage.Formats.Gif;

/// <summary>
/// How a frame's canvas region should be treated before the next frame is drawn, mirroring the GIF Graphic
/// Control Extension's disposal method (values 0-3; 0 "unspecified" and 1 "do not dispose" both mean "leave
/// as-is" in practice, so they share <see cref="DoNotDispose"/>/<see cref="None"/> here for the raw distinction
/// while behaving identically).
/// </summary>
public enum GifDisposalMethod
{
    /// <summary>No disposal specified (value 0). Treated the same as <see cref="DoNotDispose"/>.</summary>
    None,

    /// <summary>Leave the frame's pixels in place for subsequent frames (value 1).</summary>
    DoNotDispose,

    /// <summary>Clear the frame's region to the background (transparent) before the next frame is drawn (value 2).</summary>
    RestoreToBackground,

    /// <summary>Restore the frame's region to what it looked like before this frame was drawn (value 3).</summary>
    RestoreToPrevious,
}
