namespace PeachImage;

/// <summary>Base class for format-specific encode options. Format codecs derive their own options from this.</summary>
public abstract class EncoderOptions
{
    /// <summary>Whether the encoder should attempt to re-emit metadata captured on the source <see cref="Image"/>.</summary>
    public bool IncludeMetadata { get; init; } = true;
}
