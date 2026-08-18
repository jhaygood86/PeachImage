using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Formats.Webp.Decoding;

/// <summary>Converts a decoded <see cref="Image"/> to the pixel format requested via <see cref="DecoderOptions.TargetPixelFormat"/>. The actual pixel reshaping is done by the shared, SIMD-optimized <see cref="PixelFormatConversionKernels"/> — this class only owns the supported-pair dispatch (scoped to the formats <see cref="WebpDecoder"/> actually produces: Rgb24/Rgba32) and WebP's own exception type.</summary>
internal static class PixelFormatConverter
{
    /// <summary>Converts <paramref name="image"/> to <paramref name="target"/> if needed.</summary>
    public static Image ConvertIfNeeded(Image image, PixelFormat? target)
    {
        if (target is not { } targetFormat || targetFormat == image.PixelFormat)
        {
            return image;
        }

        var converted = (image.PixelFormat, targetFormat) switch
        {
            (PixelFormat.Rgb24, PixelFormat.Rgba32) => Convert(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandRgb24ToRgba32),
            (PixelFormat.Rgba32, PixelFormat.Rgb24) => Convert(image, PixelFormat.Rgb24, PixelFormatConversionKernels.NarrowRgba32ToRgb24),
            (PixelFormat.Rgb24, PixelFormat.Gray8) => Convert(image, PixelFormat.Gray8, PixelFormatConversionKernels.ComputeLumaFromRgb24),
            (PixelFormat.Rgba32, PixelFormat.Gray8) => Convert(image, PixelFormat.Gray8, PixelFormatConversionKernels.ComputeLumaFromRgba32),
            _ => throw new WebpDecodingException($"Cannot convert decoded {image.PixelFormat} pixels to requested format {targetFormat}."),
        };

        return converted;
    }

    private delegate void Reshape8(ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount);

    private static Image Convert(Image source, PixelFormat destFormat, Reshape8 kernel)
    {
        var dest = Image.Create(source.Width, source.Height, destFormat);
        kernel(source.GetPixelSpan(), dest.GetPixelSpan(), source.Width * source.Height);
        return dest;
    }
}
