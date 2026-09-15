using PeachImage.Formats.Jpeg.Decoding;

namespace PeachImage.Formats.Jpeg;

/// <summary>Decodes baseline sequential and progressive JPEG images. Used internally by <see cref="JpegCodec"/>.</summary>
internal static class JpegDecoder
{
    private const string FormatName = "jpeg";

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var (frameHeader, colorSpace, isAdobeInverted) = FrameDecoder.IdentifyFrameHeader(stream);
        var pixelFormat = colorSpace switch
        {
            JpegColorSpace.Grayscale => PixelFormat.Gray8,
            JpegColorSpace.YCbCr or JpegColorSpace.Rgb => PixelFormat.Rgb24,
            JpegColorSpace.Cmyk or JpegColorSpace.Ycck => PixelFormat.Cmyk32,
            _ => throw new JpegDecodingException($"Unsupported JPEG color space: {colorSpace}."),
        };

        return new ImageInfo(
            frameHeader.Width,
            frameHeader.Height,
            pixelFormat,
            FormatName,
            HasAlpha: pixelFormat.HasAlpha(),
            IsAdobeInvertedCmyk: isAdobeInverted,
            IsYcck: colorSpace == JpegColorSpace.Ycck);
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var jpegOptions = options as JpegDecoderOptions;
        if (jpegOptions is { KeepAdobeCmykInverted: true } && options?.TargetPixelFormat is { } targetFormat && targetFormat != PixelFormat.Cmyk32)
        {
            throw new JpegDecodingException($"{nameof(JpegDecoderOptions.KeepAdobeCmykInverted)} cannot be combined with a target pixel format other than {nameof(PixelFormat.Cmyk32)} (requested {targetFormat}).");
        }

        var frame = FrameDecoder.Decode(stream);
        Image image;
        try
        {
            image = FrameReconstructor.Reconstruct(frame, jpegOptions);
        }
        finally
        {
            foreach (var component in frame.Components)
            {
                component.Coefficients.Return();
            }
        }

        image.Metadata.HorizontalResolution = null;
        image.Metadata.VerticalResolution = null;
        foreach (var profile in frame.Metadata)
        {
            image.Metadata.Profiles.Add(profile);
        }

        bool hasAlpha = image.PixelFormat.HasAlpha();
        var result = PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
        if (!ReferenceEquals(result, image))
        {
            image.Dispose();
        }

        result.HasAlpha = hasAlpha;
        return result;
    }
}
