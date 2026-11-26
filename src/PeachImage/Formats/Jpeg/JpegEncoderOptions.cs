namespace PeachImage.Formats.Jpeg;

/// <summary>JPEG-specific encode options.</summary>
public sealed class JpegEncoderOptions : EncoderOptions
{
    /// <summary>IJG-style quality, 1 (worst) to 100 (best). Defaults to 75.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>
    /// <see cref="Quality"/> as a fraction from 0 to 1, mapped the way a browser maps the <c>quality</c> argument of <c>canvas.toBlob(type, quality)</c>:
    /// <c>Quality = round(value x 100)</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or outside 0..1 (a browser would use its default; pass that instead).</exception>
    public float QualityFraction
    {
        get => Quality / 100f;
        init
        {
            Quality = PeachImage.Internal.BrowserQuality.ToPercent(value);
        }
    }

    /// <summary>Chroma subsampling ratio. Defaults to 4:2:0, the common web default. Ignored for grayscale sources.</summary>
    public JpegChromaSubsampling Subsampling { get; init; } = JpegChromaSubsampling.Yuv420;

    /// <summary>The number of MCUs between restart markers, or 0 to disable restart markers.</summary>
    public int RestartInterval { get; init; }

    /// <summary>Whether to encode as progressive JPEG (multiple, successively refined scans) instead of baseline sequential. Defaults to false.</summary>
    public bool Progressive { get; init; }

    /// <summary>
    /// Whether to compute image-specific optimal Huffman tables via a first frequency-counting pass
    /// (ITU-T.81 Annex K.2, libjpeg's <c>optimize_coding</c>) instead of the fixed Annex K.3 standard tables.
    /// Typically saves ~2-6% file size at a given quality, at roughly 2x entropy-coding cost. Defaults to
    /// false. Currently only affects baseline (non-progressive) output — progressive AC scans are already
    /// always optimized this way regardless of this flag (see <see cref="Encoding.ProgressiveScanEncoder"/>).
    /// </summary>
    public bool OptimizeHuffmanTables { get; init; }
}
