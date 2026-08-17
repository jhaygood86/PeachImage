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

        (byte[] pixels, bool monoChrome) = GatherPixels(image);

        var encoded = Av1FrameEncoder.Encode(pixels, image.Width, image.Height, monoChrome, options.Quality);
        AvifContainerWriter.Write(stream, encoded);
    }

    /// <summary>
    /// Validates <paramref name="image"/>'s pixel format and returns a packed RGB24 (or, for
    /// <see cref="PixelFormat.Gray8"/>, Gray8) buffer <see cref="Av1FrameEncoder"/> can consume directly.
    /// A fully-opaque <see cref="PixelFormat.Rgba32"/> source is auto-downgraded to opaque RGB24 (matching
    /// the existing WebP encoder's precedent in this repo); any non-opaque alpha is rejected outright, since
    /// this encoder does not produce an alpha item in this version.
    /// </summary>
    private static (byte[] Pixels, bool MonoChrome) GatherPixels(Image image)
    {
        switch (image.PixelFormat)
        {
            case PixelFormat.Rgb24:
                return (image.GetPixelSpan().ToArray(), false);

            case PixelFormat.Gray8:
                return (image.GetPixelSpan().ToArray(), true);

            case PixelFormat.Rgba32:
                return (GatherOpaqueRgbFromRgba32(image), false);

            default:
                throw new AvifEncodingException($"AVIF encoding does not support {image.PixelFormat}; convert to Rgb24, Rgba32 (opaque), or Gray8 first.");
        }
    }

    private static byte[] GatherOpaqueRgbFromRgba32(Image image)
    {
        var rgba = image.GetPixelSpan();
        int pixelCount = image.Width * image.Height;
        var rgb = new byte[pixelCount * 3];

        for (int i = 0; i < pixelCount; i++)
        {
            int srcIdx = i * 4;
            if (rgba[srcIdx + 3] != 0xFF)
            {
                throw new AvifUnsupportedFeatureException("AVIF encoding does not support alpha channels in this version; source image has non-opaque alpha. Flatten onto a background color first.");
            }

            int dstIdx = i * 3;
            rgb[dstIdx] = rgba[srcIdx];
            rgb[dstIdx + 1] = rgba[srcIdx + 1];
            rgb[dstIdx + 2] = rgba[srcIdx + 2];
        }

        return rgb;
    }
}
