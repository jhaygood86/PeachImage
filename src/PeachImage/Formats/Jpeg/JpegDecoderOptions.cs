namespace PeachImage.Formats.Jpeg;

/// <summary>JPEG-specific decode options.</summary>
public sealed class JpegDecoderOptions : DecoderOptions
{
    /// <summary>
    /// Use fast nearest-neighbor chroma upsampling instead of the smoother (but slightly more expensive)
    /// default triangle-filter upsampling.
    /// </summary>
    public bool FastUpsampling { get; init; }
}
