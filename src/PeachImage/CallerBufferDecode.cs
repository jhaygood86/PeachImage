using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Shared.Metadata;
using PeachImage.Formats.Shared.Orientation;
using PeachImage.Formats.Shared.Scaling;

namespace PeachImage;

/// <summary>
/// The shared implementation behind <see cref="ImageDecoder"/> and <see cref="Image.DecodeInto(ReadOnlySpan{byte}, Span{byte}, out ImageMetadata, int, DecoderOptions?)"/>:
/// decodes into memory the caller owns, checking the destination against the header before any pixel work. A format with a native
/// path (JPEG) writes straight into the destination; every other format decodes to a pooled <see cref="Image"/> and is copied.
/// </summary>
internal static class CallerBufferDecode
{
    /// <summary>
    /// Decodes <paramref name="stream"/> into <paramref name="destination"/>. Returns <see langword="false"/> (decoding nothing, with
    /// <paramref name="info"/> the header's own description) when the destination is shorter than <paramref name="required"/>.
    /// </summary>
    public static unsafe bool Run(
        Stream stream, DecoderOptions? options, Span<byte> destination, int rowStride,
        out ImageInfo info, out ImageMetadata metadata, out int required)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var seekable = Image.EnsureSeekable(stream);
        long start = seekable.Position;
        var header = Image.IdentifyHeader(seekable, options, out var codec);
        seekable.Position = start;

        required = header.GetRequiredBufferSize(options, rowStride);
        if (destination.Length < required)
        {
            info = header;
            metadata = new ImageMetadata();
            return false;
        }

        if (IsNativeJpeg(codec, header, options))
        {
            fixed (byte* pointer = destination)
            {
                info = DecodeJpegNatively(seekable, codec, header, options, pointer, rowStride, out metadata);
            }

            return true;
        }

        using var image = Image.Load(seekable, options);

        int bytesPerPixel = image.PixelFormat.GetBytesPerPixel();
        int rowBytes = checked(image.Width * bytesPerPixel);
        int stride = rowStride == 0 ? rowBytes : rowStride;
        if (stride < rowBytes)
        {
            throw new InvalidOperationException("The decoded image is wider than the header declared.");
        }

        var pixels = image.GetPixelSpan();
        if (stride == rowBytes)
        {
            pixels.CopyTo(destination);
        }
        else
        {
            for (int y = 0; y < image.Height; y++)
            {
                pixels.Slice(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
            }
        }

        metadata = image.Metadata;
        info = header with
        {
            Width = image.Width,
            Height = image.Height,
            PixelFormat = image.PixelFormat,
            HasAlpha = image.HasAlpha,
            IsAnimated = image.IsAnimated,
            Orientation = options?.ApplyOrientation == true ? ImageOrientation.Normal : header.Orientation,
            HasLocation = metadata.HasLocation,
            ExifLocation = metadata.ExifLocation,
        };
        return true;
    }

    // A full-size JPEG in its natural pixel format, with nothing that needs the image as a whole afterwards.
    private static bool IsNativeJpeg(IImageCodec codec, ImageInfo header, DecoderOptions? options) =>
        codec == JpegCodec.Instance
        && options is not IFormatSelectingOptions
        && options?.PreviewAvailable is null
        && !DecodeScale.IsRequested(options)
        && (options?.ApplyOrientation != true || header.Orientation == ImageOrientation.Normal)
        && (options?.TargetPixelFormat is null || options.TargetPixelFormat == header.PixelFormat);

    private static unsafe ImageInfo DecodeJpegNatively(
        Stream stream, IImageCodec codec, ImageInfo header, DecoderOptions? options, byte* destination, int rowStride, out ImageMetadata metadata)
    {
        bool skipMetadata = options?.SkipMetadata == true;
        if (options is { MaxPixels: not null } or { HasExpansionRatioLimit: true })
        {
            Image.EnforceDeclaredLimits(codec, stream, options);
        }

        var location = default(LocationInfo);
        if (!skipMetadata && ContainerLocationScanner.Supports(codec.FormatName))
        {
            location = ContainerLocationScanner.Scan(stream, codec.FormatName);
        }

        int stride = rowStride == 0 ? header.Width * header.PixelFormat.GetBytesPerPixel() : rowStride;
        var format = JpegDecoder.DecodeInto(stream, options, destination, stride, out metadata);
        metadata.ApplyExifResolution();
        if (skipMetadata)
        {
            metadata.Profiles.Clear();
        }
        else
        {
            metadata.ScannedLocation = location;
        }

        return header with
        {
            PixelFormat = format,
            HasAlpha = format.HasAlpha(),
            IsAnimated = false,
            HasLocation = metadata.HasLocation,
            ExifLocation = metadata.ExifLocation,
        };
    }

    /// <summary>Wraps <see cref="Run"/> for the throwing entry points.</summary>
    public static ImageInfo RunOrThrow(
        Stream stream, DecoderOptions? options, Span<byte> destination, int rowStride, out ImageMetadata metadata)
    {
        if (!Run(stream, options, destination, rowStride, out var info, out metadata, out int required))
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} bytes but this image needs {required}.", nameof(destination));
        }

        return info;
    }

    public static unsafe bool RunSpan(
        ReadOnlySpan<byte> data, DecoderOptions? options, Span<byte> destination, int rowStride,
        out ImageInfo info, out ImageMetadata metadata, out int required)
    {
        fixed (byte* pointer = data)
        {
            using var stream = new UnmanagedMemoryStream(pointer, data.Length);
            return Run(stream, options, destination, rowStride, out info, out metadata, out required);
        }
    }

    public static unsafe ImageInfo RunSpanOrThrow(
        ReadOnlySpan<byte> data, DecoderOptions? options, Span<byte> destination, int rowStride, out ImageMetadata metadata)
    {
        fixed (byte* pointer = data)
        {
            using var stream = new UnmanagedMemoryStream(pointer, data.Length);
            return RunOrThrow(stream, options, destination, rowStride, out metadata);
        }
    }

    /// <summary>The bytes an image of the described output size needs at <paramref name="rowStride"/> (0 = tightly packed).</summary>
    public static int RequiredSize(int width, int height, PixelFormat format, int rowStride)
    {
        if (rowStride < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStride), rowStride, "The row stride cannot be negative.");
        }

        long rowBytes = (long)width * format.GetBytesPerPixel();
        long stride = rowStride == 0 ? rowBytes : rowStride;
        if (stride < rowBytes)
        {
            throw new ArgumentException($"The row stride {rowStride} is less than the {rowBytes} bytes one row needs.", nameof(rowStride));
        }

        long total = (stride * (height - 1)) + rowBytes;
        return total > int.MaxValue
            ? throw new ArgumentException($"The image needs {total} bytes, more than a buffer can hold.", nameof(rowStride))
            : (int)total;
    }

    /// <summary>The size and format a decode of <paramref name="info"/> under <paramref name="options"/> produces.</summary>
    public static (int Width, int Height, PixelFormat Format) OutputLayout(ImageInfo info, DecoderOptions? options)
    {
        int scale = DecodeScale.Resolve(options, info.Width, info.Height, info.Orientation);
        (int width, int height) = DecodeScale.ScaledSize(info.Width, info.Height, scale);
        if (options?.ApplyOrientation == true && ImageOrientationApplier.SwapsDimensions(info.Orientation))
        {
            (width, height) = (height, width);
        }

        return (width, height, options?.TargetPixelFormat ?? info.PixelFormat);
    }
}
