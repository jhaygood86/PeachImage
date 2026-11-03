using PeachImage.Formats.Shared.Metadata;
using PeachImage.Formats.Jpeg.Decoding;
using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Formats.Shared.Scaling;

namespace PeachImage.Formats.Jpeg;

/// <summary>Decodes baseline sequential and progressive JPEG images. Used internally by <see cref="JpegCodec"/>.</summary>
internal static class JpegDecoder
{
    private const string FormatName = "jpeg";

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var (frameHeader, colorSpace, isAdobeInverted, orientation) = FrameDecoder.IdentifyFrameHeader(stream);
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
            IsYcck: colorSpace == JpegColorSpace.Ycck,
            HasPreview: frameHeader.IsProgressive,
            Orientation: orientation);
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

        // Progressive previews reconstruct from the coefficients accumulated so far. Reconstruction only reads the
        // coefficient buffers (it never returns them), so the final decode continues to refine the same ones.
        Action<DecodedFrame>? onProgress = null;
        if (options?.PreviewAvailable is { } previewAvailable)
        {
            onProgress = partial => previewAvailable(BuildImage(partial, options, jpegOptions));
        }

        var frame = FrameDecoder.Decode(stream, progressFrameAvailable: onProgress);
        try
        {
            return BuildImage(frame, options, jpegOptions);
        }
        finally
        {
            foreach (var component in frame.Components)
            {
                component.Coefficients.Return();
            }
        }
    }

    /// <summary>Reconstructs <paramref name="frame"/> into an image in the requested pixel format, with its metadata attached. Leaves the frame's coefficient buffers untouched.</summary>
    private static Image BuildImage(DecodedFrame frame, DecoderOptions? options, JpegDecoderOptions? jpegOptions)
    {
        int scale = DecodeScale.Resolve(options, frame.FrameHeader.Width, frame.FrameHeader.Height, ImageOrientation.Normal);
        var image = FrameReconstructor.Reconstruct(frame, jpegOptions, ParallelSettings.From(options), scale);
        image.Metadata.HorizontalResolution = null;
        image.Metadata.VerticalResolution = null;
        foreach (var profile in frame.Metadata)
        {
            image.Metadata.Profiles.Add(profile);
        }

        ApplyResolution(image.Metadata);

        bool hasAlpha = image.PixelFormat.HasAlpha();
        var result = PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat, options?.FallbackCmykProfile);
        if (!ReferenceEquals(result, image))
        {
            image.Dispose();
        }

        result.HasAlpha = hasAlpha;
        return result;
    }

    /// <summary>Density from JFIF (units 1 or 2), else from EXIF, in pixels per metre; JFIF units 0 is only an aspect ratio.</summary>
    private static void ApplyResolution(ImageMetadata metadata)
    {
        foreach (var profile in metadata.Profiles)
        {
            // APP0 payload: "JFIF\0", version (2), units (1), Xdensity (2, big endian), Ydensity (2, big endian).
            if (profile.Kind == MetadataProfileKind.Jfif && profile.Data.Length >= 12 && profile.Data.AsSpan(0, 5).SequenceEqual("JFIF\0"u8)
                && ResolutionUnits.FromJfifUnit(profile.Data[7]) is { } factor)
            {
                int x = (profile.Data[8] << 8) | profile.Data[9];
                int y = (profile.Data[10] << 8) | profile.Data[11];
                if (x > 0 && y > 0)
                {
                    metadata.HorizontalResolution = x * factor;
                    metadata.VerticalResolution = y * factor;
                    return;
                }
            }
        }

        foreach (var profile in metadata.Profiles)
        {
            if (profile.Kind == MetadataProfileKind.Exif && ExifResolutionReader.Read(profile.Data) is { } density)
            {
                metadata.HorizontalResolution = density.X;
                metadata.VerticalResolution = density.Y;
                return;
            }
        }
    }
}
