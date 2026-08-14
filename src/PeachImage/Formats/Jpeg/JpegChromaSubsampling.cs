namespace PeachImage.Formats.Jpeg;

/// <summary>Chroma subsampling ratios supported when encoding a JPEG.</summary>
public enum JpegChromaSubsampling
{
    /// <summary>No subsampling: chroma sampled at full resolution.</summary>
    Yuv444,

    /// <summary>Chroma halved horizontally only.</summary>
    Yuv422,

    /// <summary>Chroma halved both horizontally and vertically (the web default).</summary>
    Yuv420,

    /// <summary>Chroma quartered horizontally, full vertically.</summary>
    Yuv411,
}
