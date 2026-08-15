namespace PeachImage.Formats.Webp.Decoding;

/// <summary>Which of WebP's two unrelated bitstream codecs a file's image chunk uses.</summary>
internal enum WebpBitstreamFormat
{
    /// <summary>VP8 (lossy), decoded via <see cref="Vp8.Vp8FrameDecoder"/>.</summary>
    Lossy,

    /// <summary>VP8L (lossless), decoded via <see cref="Vp8L.Vp8LDecoder"/>.</summary>
    Lossless,
}
