using PeachImage.Internal.Color;

namespace PeachImage;

/// <summary>
/// Encodes pixels that live in memory the caller owns, without first building an <see cref="Image"/> of them: a canvas backing store, a GPU readback, a frame
/// buffer. The counterpart of <see cref="ImageDecoder"/>.
/// </summary>
/// <remarks>
/// Any <see cref="PixelFormat"/> encodes, including the premultiplied ones. For PNG, JPEG, WebP, AVIF and JPEG XL the pixel rows (strided or not, in
/// any format) are read where they are and converted in one pass straight into the sample depth and layout the encoder writes: color is unpremultiplied and,
/// with <see cref="EncoderOptions.TargetColorSpace"/>, converted to the target space in that same pass, so there is no intermediate copy of the buffer.
/// A tightly packed whole array in a format the encoder takes as it is (an <see cref="PixelFormat.Rgba32"/> canvas going to PNG, say) is encoded in place with no copy
/// at all. For the other formats (BMP, GIF, TIFF) the rows are copied into an image first.
/// </remarks>
public static class ImageEncoder
{
    /// <summary>Encodes a buffer of pixels as <paramref name="formatName"/> (for example <c>"png"</c>) into <paramref name="destination"/>.</summary>
    /// <param name="pixels">The pixels, <paramref name="height"/> rows of <paramref name="width"/> pixels, rows <paramref name="strideBytes"/> apart.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">The pixel format of <paramref name="pixels"/>.</param>
    /// <param name="destination">The stream to write the encoded image to.</param>
    /// <param name="formatName">The output format name, as in <see cref="Image.Save(Stream, string, EncoderOptions?)"/>.</param>
    /// <param name="options">Encoder options, or <see langword="null"/> for the defaults.</param>
    /// <param name="strideBytes">The distance between row starts in bytes; 0 means tightly packed.</param>
    /// <param name="colorSpace">The color space the pixel values are in, for the file to declare and for <see cref="EncoderOptions.TargetColorSpace"/> to convert from; <see langword="null"/> means sRGB with nothing declared. Linear float data is <see cref="NamedColorSpace.LinearSrgb"/> (or another linear space).</param>
    /// <param name="metadata">Metadata to write with the image (EXIF, XMP, an ICC profile of the caller's own), or <see langword="null"/>.</param>
    /// <remarks>A span cannot be shared with parallel work, so this overload converts on the calling thread; pass a <see cref="ReadOnlyMemory{T}"/> to use every core.</remarks>
    public static void Encode(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        PixelFormat format,
        Stream destination,
        string formatName,
        EncoderOptions? options = null,
        int strideBytes = 0,
        NamedColorSpace? colorSpace = null,
        ImageMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var codec = FindCodec(formatName);
        Image.ValidateBufferLayout(pixels.Length, width, height, format, strideBytes);
        using var prepared = EncodePreparation.PrepareBuffer(default, pixels, useMemory: false, width, height, format, strideBytes, Describe(colorSpace, metadata), codec, options);
        codec.Encode(prepared.Image, destination, options);
    }

    /// <summary>Encodes a buffer of pixels, converting on every core; see <see cref="Encode(ReadOnlySpan{byte}, int, int, PixelFormat, Stream, string, EncoderOptions?, int, NamedColorSpace?, ImageMetadata?)"/>.</summary>
    public static void Encode(
        ReadOnlyMemory<byte> pixels,
        int width,
        int height,
        PixelFormat format,
        Stream destination,
        string formatName,
        EncoderOptions? options = null,
        int strideBytes = 0,
        NamedColorSpace? colorSpace = null,
        ImageMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var codec = FindCodec(formatName);
        Image.ValidateBufferLayout(pixels.Length, width, height, format, strideBytes);
        using var prepared = EncodePreparation.PrepareBuffer(pixels, default, useMemory: true, width, height, format, strideBytes, Describe(colorSpace, metadata), codec, options);
        codec.Encode(prepared.Image, destination, options);
    }

    private static IImageCodec FindCodec(string formatName)
    {
        ArgumentNullException.ThrowIfNull(formatName);
        return Image.FindCodecByFormatName(formatName)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode format '{formatName}'.", formatName);
    }

    private static ImageMetadata Describe(NamedColorSpace? colorSpace, ImageMetadata? metadata)
    {
        var described = new ImageMetadata();
        if (metadata is not null)
        {
            described.HorizontalResolution = metadata.HorizontalResolution;
            described.VerticalResolution = metadata.VerticalResolution;
            foreach (var profile in metadata.Profiles)
            {
                described.Profiles.Add(profile);
            }
        }

        if (colorSpace is { } space)
        {
            ColorConverter.Retag(described, space, null);
        }

        return described;
    }
}
