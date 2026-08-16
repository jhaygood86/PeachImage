namespace PeachImage.Formats.Webp;

/// <summary>Requested tradeoff between WebP lossless encode speed and output size.</summary>
public enum WebpCompressionLevel
{
    /// <summary>Fastest encode, largest output.</summary>
    Fastest,

    /// <summary>A balanced default.</summary>
    Default,

    /// <summary>Slowest encode, smallest output.</summary>
    SmallestSize,
}
