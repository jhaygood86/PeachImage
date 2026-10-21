namespace PeachImage;

/// <summary>
/// Implemented by decoder options whose format cannot be recognized from the stream's leading bytes (raw CCITT fax
/// data, JBIG2 streams embedded in a PDF): <see cref="Image.Load(Stream, DecoderOptions?)"/> uses the selected codec
/// directly instead of sniffing.
/// </summary>
internal interface IFormatSelectingOptions
{
    /// <summary>The codec to decode with, or <see langword="null"/> to fall back to sniffing.</summary>
    IImageCodec? Codec { get; }
}
