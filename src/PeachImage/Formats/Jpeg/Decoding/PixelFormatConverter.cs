using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>Converts a decoded <see cref="Image"/> between the small set of pixel formats a caller may request via <see cref="DecoderOptions.TargetPixelFormat"/>. The actual pixel reshaping is done by the shared, SIMD-optimized <see cref="PixelFormatConversionKernels"/> — this class only owns the supported-pair dispatch and JPEG's own exception type.</summary>
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
            (PixelFormat.Gray8, PixelFormat.Rgb24) => Convert(image, PixelFormat.Rgb24, PixelFormatConversionKernels.ExpandGray8ToRgb24),
            (PixelFormat.Gray8, PixelFormat.Rgba32) => Convert(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandGray8ToRgba32),
            (PixelFormat.Rgb24, PixelFormat.Rgba32) => Convert(image, PixelFormat.Rgba32, PixelFormatConversionKernels.ExpandRgb24ToRgba32),
            (PixelFormat.Rgba32, PixelFormat.Rgb24) => Convert(image, PixelFormat.Rgb24, PixelFormatConversionKernels.NarrowRgba32ToRgb24),
            (PixelFormat.Rgb24, PixelFormat.Gray8) => Convert(image, PixelFormat.Gray8, PixelFormatConversionKernels.ComputeLumaFromRgb24),
            (PixelFormat.Rgba32, PixelFormat.Gray8) => Convert(image, PixelFormat.Gray8, PixelFormatConversionKernels.ComputeLumaFromRgba32),

            // `image.PixelFormat` is always standard (non-inverted) CMYK here regardless of source encoding --
            // FrameReconstructor already undoes Adobe's inverted-CMYK convention and YCCK's transform before
            // producing PixelFormat.Cmyk32, so this one conversion point covers all three uniformly.
            (PixelFormat.Cmyk32, PixelFormat.Rgba32) => ConvertCmykToRgba32(image),

            _ => throw new JpegDecodingException($"Cannot convert decoded {image.PixelFormat} pixels to requested format {targetFormat}."),
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

    /// <summary>
    /// Uses the image's embedded ICC profile (<see cref="IccDeviceToSrgbConverter"/>) for a colorimetric
    /// CMYK->RGBA32 conversion when one is present and usable, falling back to the naive additive formula
    /// (<see cref="PixelFormatConversionKernels.ConvertCmyk32ToRgba32"/>) otherwise -- never throws on a
    /// missing, corrupt, or unsupported profile.
    /// </summary>
    private static Image ConvertCmykToRgba32(Image image)
    {
        var dest = Image.Create(image.Width, image.Height, PixelFormat.Rgba32);
        int pixelCount = image.Width * image.Height;
        var source = image.GetPixelSpan();
        var destination = dest.GetPixelSpan();

        foreach (var profile in image.Metadata.Profiles)
        {
            if (profile.Kind == MetadataProfileKind.Icc && IccDeviceToSrgbConverter.TryConvert(source, destination, pixelCount, profile.Data))
            {
                return dest;
            }
        }

        PixelFormatConversionKernels.ConvertCmyk32ToRgba32(source, destination, pixelCount);
        return dest;
    }
}
