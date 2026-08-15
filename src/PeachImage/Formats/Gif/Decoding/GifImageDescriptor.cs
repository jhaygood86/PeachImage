namespace PeachImage.Formats.Gif.Decoding;

/// <summary>The parsed contents of an Image Descriptor (0x2C) plus its Local Color Table, if any.</summary>
internal sealed class GifImageDescriptor
{
    public required int Left { get; init; }

    public required int Top { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required bool Interlaced { get; init; }

    /// <summary>Flat RGB triples for the Local Color Table, or empty if the Local Color Table Flag was unset (use the Global Color Table instead).</summary>
    public required byte[] LocalColorTable { get; init; }
}
