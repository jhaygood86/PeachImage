using PeachImage.Formats.Avif.Container;
using PeachImage.Formats.Avif.Encoder.Av1;
using PeachImage.Formats.Avif.Internal;

namespace PeachImage.Formats.Avif.Encoder;

/// <summary>
/// Orchestrates a full AVIF encode: validates the source image, converts it to the packed pixel buffer
/// <see cref="Av1FrameEncoder"/> expects, and hands the result to <see cref="AvifContainerWriter"/>. Used
/// internally by <see cref="AvifEncoder"/>.
/// </summary>
internal static class AvifImageEncoder
{
    public static void Encode(Image image, Stream stream, AvifEncoderOptions options)
    {
        if (image.Width > AvifDecodingLimits.MaxDimension || image.Height > AvifDecodingLimits.MaxDimension
            || (long)image.Width * image.Height > AvifDecodingLimits.MaxPixelCount)
        {
            throw new AvifEncodingException($"AVIF image {image.Width}x{image.Height} exceeds the maximum supported dimensions.");
        }

        (byte[] pixels, bool monoChrome, byte[]? alpha) = GatherPixels(image);

        var encoded = Av1FrameEncoder.Encode(pixels, image.Width, image.Height, monoChrome, options.Quality, options.Lossless);

        // Alpha is encoded as its own independent monochrome AV1 image -- Av1FrameEncoder's existing
        // monoChrome path already produces exactly that, so no encoder-internal alpha support is needed,
        // only a second Encode call and container-level muxing (see AvifContainerWriter's remarks). Alpha is
        // lossless whenever the color plane is, so a Lossless request never silently loses transparency.
        Av1EncodedFrame? alphaEncoded = alpha is null
            ? null
            : Av1FrameEncoder.Encode(alpha, image.Width, image.Height, monoChrome: true, options.Quality, options.Lossless);

        AvifContainerWriter.Write(stream, encoded, alphaEncoded);
    }

    /// <summary>
    /// Validates <paramref name="image"/>'s pixel format and returns a packed RGB24 (or, for
    /// <see cref="PixelFormat.Gray8"/>, Gray8) buffer <see cref="Av1FrameEncoder"/> can consume directly,
    /// plus a separate Gray8 alpha plane if <paramref name="image"/> is <see cref="PixelFormat.Rgba32"/> with
    /// any non-opaque pixel (<see langword="null"/> otherwise -- including for a fully-opaque Rgba32 source,
    /// which is auto-downgraded to plain RGB24 with no alpha item at all, matching the existing WebP
    /// encoder's precedent in this repo and avoiding an unnecessary second AV1 image for the common case).
    /// </summary>
    private static (byte[] Pixels, bool MonoChrome, byte[]? Alpha) GatherPixels(Image image)
    {
        switch (image.PixelFormat)
        {
            case PixelFormat.Rgb24:
                return (image.GetPixelSpan().ToArray(), false, null);

            case PixelFormat.Gray8:
                return (image.GetPixelSpan().ToArray(), true, null);

            case PixelFormat.Rgba32:
                return GatherRgbAndAlphaFromRgba32(image);

            default:
                throw new AvifEncodingException($"AVIF encoding does not support {image.PixelFormat}; convert to Rgb24, Rgba32, or Gray8 first.");
        }
    }

    private static (byte[] Pixels, bool MonoChrome, byte[]? Alpha) GatherRgbAndAlphaFromRgba32(Image image)
    {
        var rgba = image.GetPixelSpan();
        int pixelCount = image.Width * image.Height;
        var rgb = new byte[pixelCount * 3];
        var alpha = new byte[pixelCount];
        bool hasTransparency = false;

        for (int i = 0; i < pixelCount; i++)
        {
            int srcIdx = i * 4;
            int dstIdx = i * 3;
            rgb[dstIdx] = rgba[srcIdx];
            rgb[dstIdx + 1] = rgba[srcIdx + 1];
            rgb[dstIdx + 2] = rgba[srcIdx + 2];

            byte a = rgba[srcIdx + 3];
            alpha[i] = a;
            hasTransparency |= a != 0xFF;
        }

        return (rgb, false, hasTransparency ? alpha : null);
    }
}
