namespace PeachImage.Formats.Webp.Decoding.Vp8.ColorConversion;

/// <summary>Converts a single studio/limited-range YUV sample triple to RGB.</summary>
internal interface IVp8ColorConverter
{
    void Convert(byte y, byte u, byte v, Span<byte> rgb);
}