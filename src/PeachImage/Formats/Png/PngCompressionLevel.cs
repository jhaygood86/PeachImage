namespace PeachImage.Formats.Png;

/// <summary>Requested tradeoff between PNG encode speed and output size.</summary>
public enum PngCompressionLevel
{
    /// <summary>Fastest encode, largest output.</summary>
    Fastest,

    /// <summary>A balanced default.</summary>
    Default,

    /// <summary>Slowest encode, smallest output.</summary>
    SmallestSize,
}
