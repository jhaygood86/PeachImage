namespace PeachImage;

/// <summary>Lightweight image dimensions/format info obtainable without decoding full pixel data.</summary>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
/// <param name="PixelFormat">The pixel format a full decode would produce.</param>
/// <param name="FormatName">The name of the format that produced this info, e.g. <c>"jpeg"</c>.</param>
public readonly record struct ImageInfo(int Width, int Height, PixelFormat PixelFormat, string FormatName);
