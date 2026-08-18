namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// The parsed <c>ANIM</c> chunk plus the canvas size from <see cref="WebpContainerPrelude"/>: everything
/// known about an animated WebP file before any frame is decoded.
/// </summary>
internal readonly record struct WebpAnimationHeader(int CanvasWidth, int CanvasHeight, int LoopCount);
