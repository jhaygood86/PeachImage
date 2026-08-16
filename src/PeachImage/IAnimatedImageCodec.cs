namespace PeachImage;

/// <summary>
/// An <see cref="IImageCodec"/> that also supports multi-frame animation. <see cref="AnimatedImage"/>
/// dispatches through whichever of <see cref="Image"/>'s built-in codecs implement this — the same way
/// <see cref="Image"/> itself dispatches through every <see cref="IImageCodec"/> — instead of exposing
/// any single format's animation support directly.
/// </summary>
internal interface IAnimatedImageCodec : IImageCodec
{
    /// <summary>Fully decodes every frame of <paramref name="stream"/>'s animation into an in-memory <see cref="AnimatedImage"/>.</summary>
    AnimatedImage DecodeAnimation(Stream stream, DecoderOptions? options = null);

    /// <summary>Encodes every frame of <paramref name="image"/> and writes the result to <paramref name="stream"/>.</summary>
    void EncodeAnimation(AnimatedImage image, Stream stream, EncoderOptions? options = null);
}
