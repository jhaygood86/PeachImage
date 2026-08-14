namespace PeachImage;

/// <summary>Base class for format-specific decode options. Format codecs derive their own options from this.</summary>
public abstract class DecoderOptions
{
    /// <summary>
    /// The pixel format the caller wants the decoded <see cref="Image"/> to use, analogous to stb_image's
    /// <c>req_comp</c>. When <see langword="null"/>, the decoder produces whatever format is most natural
    /// for the source data.
    /// </summary>
    public PixelFormat? TargetPixelFormat { get; init; }
}
