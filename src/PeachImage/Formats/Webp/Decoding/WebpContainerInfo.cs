namespace PeachImage.Formats.Webp.Decoding;

/// <summary>The result of parsing a WebP file's RIFF container: which inner bitstream it carries, plus any container-level extras.</summary>
internal sealed class WebpContainerInfo
{
    /// <summary>Whether the image chunk is VP8 (lossy) or VP8L (lossless).</summary>
    public required WebpBitstreamFormat Format { get; init; }

    /// <summary>The raw payload of the <c>VP8 </c>/<c>VP8L</c> chunk, untouched — handed to the matching bitstream decoder.</summary>
    public required byte[] BitstreamData { get; init; }

    /// <summary>The raw payload of the <c>ALPH</c> chunk, if the file is extended-format lossy-with-alpha. Always <see langword="null"/> for lossless images (VP8L carries its own alpha).</summary>
    public byte[]? AlphaData { get; init; }

    /// <summary>The VP8X chunk's declared canvas width, or <see langword="null"/> for simple-format files (no VP8X chunk).</summary>
    public int? CanvasWidth { get; init; }

    /// <summary>The VP8X chunk's declared canvas height, or <see langword="null"/> for simple-format files (no VP8X chunk).</summary>
    public int? CanvasHeight { get; init; }
}
