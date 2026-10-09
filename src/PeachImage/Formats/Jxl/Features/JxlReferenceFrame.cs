namespace PeachImage.Formats.Jxl.Features;

/// <summary>
/// A frame kept for later frames to refer to (as a patch source, or later as a blending base): float colour planes
/// followed by the extra channels, all <see cref="Width"/> samples wide.
/// </summary>
internal sealed class JxlReferenceFrame
{
    public JxlReferenceFrame(int width, int height, float[][] planes, bool isInXyb)
    {
        Width = width;
        Height = height;
        Planes = planes;
        IsInXyb = isInXyb;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Three colour planes then one plane per extra channel.</summary>
    public float[][] Planes { get; }

    /// <summary>Whether the colour planes are still in the frame's own colour space (XYB) rather than the output encoding.</summary>
    public bool IsInXyb { get; }
}

/// <summary>State that flows from one frame of a codestream to the next: the saved reference frames and the noise seed counters.</summary>
internal sealed class JxlDecoderState
{
    public const int ReferenceSlots = 4;

    public JxlReferenceFrame?[] References { get; } = new JxlReferenceFrame?[ReferenceSlots];

    /// <summary>XYB low-frequency (1:8, 1:64, ...) images from DC frames, indexed by level - 1, for frames flagged to use one.</summary>
    public JxlReferenceFrame?[] DcFrames { get; } = new JxlReferenceFrame?[4];

    /// <summary>Number of visible frames started so far, including the one being decoded.</summary>
    public uint VisibleFrameIndex { get; set; }

    /// <summary>Number of invisible frames since the last visible one, including the one being decoded.</summary>
    public uint NonVisibleFrameIndex { get; set; }
}
