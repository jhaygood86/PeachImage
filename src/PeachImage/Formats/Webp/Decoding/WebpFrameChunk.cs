namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// One <c>ANMF</c> chunk's fixed 16-byte frame header: position/size of this frame's sub-rectangle on the
/// VP8X canvas, how long to display it, and how to composite it (<see cref="Blend"/>) and dispose of it
/// (<see cref="DisposeToBackground"/>) relative to the next frame. Does not include the nested
/// <c>ALPH</c>/<c>VP8 </c>/<c>VP8L</c> image data that follows the header within the same ANMF chunk.
/// </summary>
internal readonly record struct WebpFrameChunk(int X, int Y, int Width, int Height, TimeSpan Duration, bool DisposeToBackground, bool Blend);
