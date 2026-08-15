namespace PeachImage.Formats.Gif.Decoding;

/// <summary>The parsed contents of a Graphic Control Extension (0x21 0xF9), which precedes and applies to the next Image Descriptor.</summary>
internal sealed class GifGraphicControlExtension
{
    public static readonly GifGraphicControlExtension Default = new()
    {
        Disposal = GifDisposalMethod.None,
        TransparentColorIndex = null,
        Delay = TimeSpan.Zero,
    };

    public required GifDisposalMethod Disposal { get; init; }

    public required int? TransparentColorIndex { get; init; }

    public required TimeSpan Delay { get; init; }
}
