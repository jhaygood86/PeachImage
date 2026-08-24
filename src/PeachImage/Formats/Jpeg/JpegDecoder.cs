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

        var frameHeader = FrameDecoder.IdentifyFrameHeader(stream);
        var pixelFormat = frameHeader.Components.Length switch
        {
            1 => PixelFormat.Gray8,
            3 => PixelFormat.Rgb24,
            4 => PixelFormat.Cmyk32,
            _ => throw new JpegDecodingException($"Unsupported JPEG component count: {frameHeader.Components.Length}."),
        };

        return new ImageInfo(frameHeader.Width, frameHeader.Height, pixelFormat, FormatName, HasAlpha: pixelFormat.HasAlpha());
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var jpegOptions = options as JpegDecoderOptions;
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
