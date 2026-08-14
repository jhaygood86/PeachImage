using PeachImage.Formats.Jpeg.Decoding;

namespace PeachImage.Formats.Jpeg;

/// <summary>Decodes baseline sequential and progressive JPEG images.</summary>
public sealed class JpegDecoder : IImageDecoder
{
    /// <inheritdoc/>
    public string FormatName => "jpeg";

    /// <inheritdoc/>
    public IReadOnlyList<string> FileExtensions { get; } = ["jpg", "jpeg", "jpe", "jfif"];

    /// <inheritdoc/>
    public IReadOnlyList<string> MimeTypes { get; } = ["image/jpeg"];

    /// <inheritdoc/>
    public int HeaderSize => 3;

    /// <inheritdoc/>
    public bool IsSupportedFileFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

    /// <inheritdoc/>
    public ImageInfo Identify(Stream stream)
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

        return new ImageInfo(frameHeader.Width, frameHeader.Height, pixelFormat, FormatName);
    }

    /// <inheritdoc/>
    public Image Decode(Stream stream, DecoderOptions? options = null)
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

        return PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
    }
}
