namespace PeachImage.Formats.Jpeg;

/// <summary>JPEG-specific encode options.</summary>
public sealed class JpegEncoderOptions : EncoderOptions
{
    /// <summary>IJG-style quality, 1 (worst) to 100 (best). Defaults to 75.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>Chroma subsampling ratio. Defaults to 4:2:0, the common web default. Ignored for grayscale sources.</summary>
    public JpegChromaSubsampling Subsampling { get; init; } = JpegChromaSubsampling.Yuv420;

    /// <summary>The number of MCUs between restart markers, or 0 to disable restart markers.</summary>
    public int RestartInterval { get; init; }

    /// <summary>Whether to encode as progressive JPEG (multiple, successively refined scans) instead of baseline sequential. Defaults to false.</summary>
    public bool Progressive { get; init; }
}
