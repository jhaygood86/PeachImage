namespace PeachImage.Formats.Png;

/// <summary>Controls whether <see cref="PngEncoderOptions"/> encodes an indexed (PLTE) palette. Only ever affects <see cref="PixelFormat.Rgb24"/>/<see cref="PixelFormat.Rgba32"/> sources — grayscale and 16-bit-per-channel sources always encode via their existing color type.</summary>
public enum PngColorMode
{
    /// <summary>
    /// Indexed color (PLTE) when the source has at most <see cref="PngEncoderOptions.MaxColors"/> distinct
    /// opaque colors and its alpha channel, if any, is strictly binary (every pixel fully opaque or fully
    /// transparent) — the only case indexed color can represent losslessly. Otherwise falls back to
    /// grayscale/truecolor, so this mode never discards image data.
    /// </summary>
    Auto,

    /// <summary>
    /// Always indexed color (PLTE), quantizing down to <see cref="PngEncoderOptions.MaxColors"/> entries via
    /// median-cut if the source has more distinct colors than that, and collapsing any partial alpha to fully
    /// opaque/transparent at the same 128 threshold GIF encoding uses. Both are lossy when they apply —
    /// an explicit trade of fidelity for a smaller file, mirroring <see cref="Gif.GifEncoderOptions"/>.
    /// </summary>
    Indexed,

    /// <summary>Always grayscale/truecolor — the only behavior available before indexed-color encoding existed.</summary>
    Truecolor,
}
