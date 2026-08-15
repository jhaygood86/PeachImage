namespace PeachImage.Formats.Gif.Decoding;

/// <summary>The parsed GIF signature, Logical Screen Descriptor, and (if present) Global Color Table.</summary>
internal sealed class GifHeader
{
    public required string Version { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int BackgroundColorIndex { get; init; }

    /// <summary>Flat RGB triples (length = entry count * 3), or empty if the Global Color Table Flag was unset.</summary>
    public required byte[] GlobalColorTable { get; init; }
}
