using System.Buffers;
using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Internal.Color;

/// <summary>The image-level driver behind <c>Image.ConvertColorSpace</c> and the transfer-function options of <c>Image.ConvertTo</c>.</summary>
internal static class ColorConverter
{
    public static Image ConvertColorSpace(Image image, NamedColorSpace? targetNamed, IccColorProfile? targetProfile, ColorConversionOptions? options)
    {
        options ??= new ColorConversionOptions();
        if (image.PixelFormat is PixelFormat.Cmyk32 or PixelFormat.Ycck32)
        {
            throw new NotSupportedException("A CMYK image has no RGB color space to convert from; use Image.ConvertToSrgb first.");
        }

        var format = options.TargetPixelFormat ?? RgbCounterpart(image.PixelFormat);
        if (!Enum.IsDefined(format) || format is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32 or PixelFormat.Cmyk32 or PixelFormat.Ycck32)
        {
            throw new ArgumentException($"{format} cannot hold the result of a color space conversion; use an RGB or RGBA pixel format.", nameof(options));
        }

        if (options.GamutHandling == GamutHandling.Preserve && !format.IsFloat())
        {
            throw new ArgumentException($"GamutHandling.Preserve keeps values outside 0..1, which {format} cannot hold; set TargetPixelFormat to a floating-point format such as RgbaF32.", nameof(options));
        }

        var (sourceProfile, sourceSpace) = ResolveSource(image, options);
        var transform = ColorTransform.Create(
            sourceProfile,
            sourceSpace,
            targetProfile,
            targetNamed ?? NamedColorSpace.Srgb,
            clip: options.GamutHandling == GamutHandling.Clip,
            options.RenderingIntent,
            toneMap: options.HdrToneMapping == HdrToneMapping.ToSdr,
            peakNits: options.HdrPeakNits);

        var parallel = new ParallelSettings(options.MaxDegreeOfParallelism, options.TaskScheduler);
        return ImagePixelConverter.ConvertWithTransform(image, format, parallel, transform.Apply);
    }

    /// <summary>Replaces the color-space declarations on <paramref name="metadata"/> with ones for the target.</summary>
    /// <summary>Removes every color-space declaration (ICC profile, cICP code points, gAMA, cHRM, sRGB): what a reader then assumes is sRGB.</summary>
    public static void DropColorDeclarations(ImageMetadata metadata)
    {
        for (int i = metadata.Profiles.Count - 1; i >= 0; i--)
        {
            if (metadata.Profiles[i].Kind is MetadataProfileKind.Icc or MetadataProfileKind.Cicp or MetadataProfileKind.Gamma or MetadataProfileKind.Chromaticity or MetadataProfileKind.Srgb)
            {
                metadata.Profiles.RemoveAt(i);
            }
        }
    }

    /// <summary>Replaces the color-space declarations on <paramref name="metadata"/> with ones for the target.</summary>
    public static void Retag(ImageMetadata metadata, NamedColorSpace? named, IccColorProfile? profile)
    {
        DropColorDeclarations(metadata);
        if (profile is not null)
        {
            metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = profile.ProfileBytes.ToArray() });
            if (profile.Identify() is { } identified)
            {
                metadata.Cicp = CicpCode.Of(identified);
            }

            return;
        }

        // sRGB needs no profile (no declaration means sRGB); every other built-in space gets its ICC profile when one can describe it.
        // The code points always go in, so a format that carries them (PNG cICP, AVIF nclx) says so exactly.
        var space = named!.Value;
        if (space != NamedColorSpace.Srgb && ColorProfiles.HasProfile(space))
        {
            metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(space).ToArray() });
        }

        metadata.Cicp = CicpCode.Of(space);
    }

    public static Image ConvertWithTransferOptions(Image image, PixelFormat target, PixelConversionOptions options)
    {
        if (options.EncodeSrgb && options.DecodeSrgb)
        {
            throw new ArgumentException("EncodeSrgb and DecodeSrgb are mutually exclusive.", nameof(options));
        }

        var parallel = new ParallelSettings(options.MaxDegreeOfParallelism, options.TaskScheduler);
        if (!options.EncodeSrgb && !options.DecodeSrgb)
        {
            return ImagePixelConverter.Convert(image, target, parallel);
        }

        bool encode = options.EncodeSrgb;
        return ImagePixelConverter.ConvertWithTransform(image, target, parallel, (rgba, width) =>
        {
            var color = rgba.AsSpan(0, width * 4);
            if (encode)
            {
                InterleavedColorKernels.FromLinear(TransferFunction.Srgb, 0, color);
            }
            else
            {
                InterleavedColorKernels.ToLinear(TransferFunction.Srgb, 0, color);
            }
        });
    }


    private static PixelFormat RgbCounterpart(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => PixelFormat.Rgb24,
        PixelFormat.Gray16 => PixelFormat.Rgb48,
        PixelFormat.GrayF32 => PixelFormat.RgbF32,
        _ => format,
    };

    internal static (IccColorProfile? Profile, NamedColorSpace Space) ResolveSource(Image image, ColorConversionOptions options) => ResolveSource(image.Metadata, options);

    internal static (IccColorProfile? Profile, NamedColorSpace Space) ResolveSource(ImageMetadata metadata, ColorConversionOptions options)
    {
        if (options.SourceProfile is { } given)
        {
            return (given, options.AssumedSourceColorSpace);
        }

        if (options.SourceColorSpace is { } named)
        {
            return (null, named);
        }

        if (metadata.GetIccColorProfile() is { DataColorSpace: IccColorSpace.Rgb } embedded)
        {
            return (embedded, options.AssumedSourceColorSpace);
        }

        if (metadata.Cicp?.ToNamedColorSpace() is { } fromCode)
        {
            return (null, fromCode);
        }

        return (null, options.AssumedSourceColorSpace);
    }
}
