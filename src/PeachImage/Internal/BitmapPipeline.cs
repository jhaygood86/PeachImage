using PeachImage.Formats.Shared.Compositing;
using PeachImage.Internal.Color;

namespace PeachImage.Internal;

/// <summary>Applies a <see cref="BitmapRequest"/> to a decoded image: orientation, crop, color conversion, resize, premultiplication.</summary>
internal static class BitmapPipeline
{
    public static DecoderOptions DecoderOptionsFor(BitmapRequest request) => new()
    {
        ApplyOrientation = request.Orientation != BitmapOrientation.None,
        MaxPixels = request.MaxPixels,
        AllowedFormats = request.AllowedFormats,
        MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
        TaskScheduler = request.TaskScheduler,
    };

    /// <summary>Processes <paramref name="decoded"/>, taking ownership of it: the caller disposes only the result.</summary>
    public static Image Process(Image decoded, BitmapRequest request)
    {
        var image = decoded;
        try
        {
            if (request.Orientation == BitmapOrientation.FlipY)
            {
                image = Next(image, image.ApplyOrientation(ImageOrientation.MirrorVertical));
            }

            if (request.Crop is { } region)
            {
                image = Next(image, CropTo(image, region));
            }

            image = Next(image, ConvertColor(image, request));
            image = Next(image, Resize(image, request));
            return Next(image, FinalFormat(image, request));
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    // Disposes the image a step replaced, unless the step returned the very same instance.
    private static Image Next(Image previous, Image next)
    {
        if (!ReferenceEquals(previous, next))
        {
            previous.Dispose();
        }

        return next;
    }

    private static Image CropTo(Image image, ImageRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException("The crop region must have a positive width and height.", nameof(region));
        }

        if (region.X < 0 || region.Y < 0 || (long)region.X + region.Width > image.Width || (long)region.Y + region.Height > image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), region, $"The crop region lies outside the {image.Width}x{image.Height} image.");
        }

        if (region.X == 0 && region.Y == 0 && region.Width == image.Width && region.Height == image.Height)
        {
            return image;
        }

        return image.Crop(region);
    }

    private static Image ConvertColor(Image image, BitmapRequest request)
    {
        var target = request.TargetColorSpace;
        if (request.ColorSpaceConversion == BitmapColorSpaceConversion.None)
        {
            ColorConverter.Retag(image.Metadata, target, null);
            return image;
        }

        var (profile, space) = ColorConverter.ResolveSource(image, new ColorConversionOptions());
        var named = profile is null ? space : profile.Identify();
        if (named == target && image.PixelFormat is not (PixelFormat.Cmyk32 or PixelFormat.Ycck32))
        {
            // Already in the target space; only the label may need to say so.
            ColorConverter.Retag(image.Metadata, target, null);
            return image;
        }

        if (image.PixelFormat is PixelFormat.Cmyk32 or PixelFormat.Ycck32)
        {
            // Ink data has no RGB space of its own: take it to sRGB through its profile first, then on to the target.
            var rgba = image.ConvertToSrgb();
            if (target == NamedColorSpace.Srgb)
            {
                ColorConverter.Retag(rgba.Metadata, target, null);
                return rgba;
            }

            var converted = rgba.ConvertColorSpace(target, ColorOptions(rgba, request));
            rgba.Dispose();
            return converted;
        }

        return image.ConvertColorSpace(target, ColorOptions(image, request));
    }

    private static ColorConversionOptions ColorOptions(Image image, BitmapRequest request)
    {
        bool hdrSource = image.Metadata.GetColorSpace()?.IsHdr == true;
        bool floatResult = image.PixelFormat.IsFloat() || request.OutputFormat?.IsFloat() == true;
        var tone = request.HdrToneMapping ?? (floatResult ? HdrToneMapping.Preserve : HdrToneMapping.ToSdr);
        var options = new ColorConversionOptions
        {
            GamutHandling = floatResult || (hdrSource && tone == HdrToneMapping.Preserve) ? GamutHandling.Preserve : GamutHandling.Clip,
            HdrToneMapping = tone,
            HdrPeakNits = request.HdrPeakNits,
            MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
            TaskScheduler = request.TaskScheduler,
        };

        // Kept headroom needs a float working format; the final conversion takes it to the requested one.
        if (hdrSource && tone == HdrToneMapping.Preserve && !image.PixelFormat.IsFloat())
        {
            options.TargetPixelFormat = request.OutputFormat is { } o && o.IsFloat() ? o : (image.PixelFormat.HasAlpha() ? PixelFormat.RgbaF32 : PixelFormat.RgbF32);
        }

        return options;
    }

    private static Image Resize(Image image, BitmapRequest request)
    {
        if (request.ResizeWidth is null && request.ResizeHeight is null)
        {
            return image;
        }

        int width, height;
        if (request.ResizeWidth is { } w && request.ResizeHeight is { } h)
        {
            width = w;
            height = h;
        }
        else if (request.ResizeWidth is { } onlyWidth)
        {
            width = onlyWidth;
            height = Math.Max(1, (int)Math.Round((double)image.Height * onlyWidth / image.Width));
        }
        else
        {
            height = request.ResizeHeight!.Value;
            width = Math.Max(1, (int)Math.Round((double)image.Width * height / image.Height));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("The resize width and height must be positive.", nameof(request));
        }

        var filter = request.ResizeQuality switch
        {
            ResizeQuality.Pixelated => ResamplingFilter.NearestNeighbor,
            ResizeQuality.Low => ResamplingFilter.Bilinear,
            ResizeQuality.Medium => ResamplingFilter.Bicubic,
            _ => ResamplingFilter.Lanczos3,
        };

        return image.Resize(width, height, new ResizeOptions
        {
            Filter = filter,
            Mode = ResizeMode.Exact,
            MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
            TaskScheduler = request.TaskScheduler,
        });
    }

    private static Image FinalFormat(Image image, BitmapRequest request)
    {
        var format = request.OutputFormat ?? image.PixelFormat;
        if (request.OutputFormat is null && image.PixelFormat.HasAlpha() && !image.PixelFormat.IsPremultiplied()
            && request.PremultiplyAlpha != BitmapPremultiply.None)
        {
            format = image.PixelFormat.ToPremultipliedAlpha();
        }

        if (request.OutputFormat is { } explicitFormat && request.PremultiplyAlpha == BitmapPremultiply.None && explicitFormat.IsPremultiplied())
        {
            throw new ArgumentException($"PremultiplyAlpha is None but OutputFormat {explicitFormat} is premultiplied.", nameof(request));
        }

        if (image.PixelFormat is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32 && request.OutputFormat is null)
        {
            return image;
        }

        return image.ConvertTo(format);
    }
}
