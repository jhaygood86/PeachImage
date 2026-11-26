using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Internal.Color;

/// <summary>
/// Brings an image to the form an encoder accepts, in one fused pass: the source pixel format (16-bit, half or 32-bit float, premultiplied) is read
/// row by row, optionally color-converted, and written in the format's carrier (the sample depth and layout it writes), with alpha unpremultiplied
/// on the way. Images an encoder already accepts, with no color request, pass through untouched, so 8-bit encodes are byte-identical to before.
/// </summary>
internal static class EncodePreparation
{
    public readonly struct Prepared : IDisposable
    {
        private readonly Image? owned;

        public Prepared(Image image, Image? owned)
        {
            Image = image;
            this.owned = owned;
        }

        public Image Image { get; }

        public void Dispose() => owned?.Dispose();
    }

    /// <summary>What preparing a source for a managed encoder amounts to.</summary>
    /// <param name="Carrier">The pixel format the encoder is handed.</param>
    /// <param name="Passthrough">The source already is the carrier and nothing converts: the encoder takes it as it is.</param>
    /// <param name="Convert">Whether the pixels are converted to <paramref name="TargetNamed"/> or <paramref name="TargetProfile"/>.</param>
    /// <param name="TargetNamed">The named target space, when converting.</param>
    /// <param name="TargetProfile">The ICC target profile, when converting to one.</param>
    /// <param name="Transform">The color transform, when converting.</param>
    /// <param name="Unpremultiply">Whether a premultiplied source is divided by alpha on the way.</param>
    internal readonly record struct Plan(PixelFormat Carrier, bool Passthrough, bool Convert, NamedColorSpace? TargetNamed, IccColorProfile? TargetProfile, ColorTransform? Transform, bool Unpremultiply);

    /// <summary>Whether <paramref name="codec"/> is one the preparation manages (reads any pixel format for, and picks a carrier).</summary>
    public static bool IsManaged(IImageCodec codec) => codec.FormatName is "png" or "jpeg" or "webp" or "avif" or "jxl";

    public static Plan MakePlan(PixelFormat source, ImageMetadata metadata, IImageCodec codec, EncoderOptions? options)
    {
        string format = codec.FormatName;
        var precision = options?.Precision ?? EncodePrecision.Auto;

        // Resolve what the pixels should end up described as.
        NamedColorSpace? targetNamed = options?.TargetColorSpace;
        IccColorProfile? targetProfile = options?.TargetColorProfile;
        if (targetProfile is not null)
        {
            targetNamed = null;
        }

        var (sourceProfile, sourceSpace) = ColorConverter.ResolveSource(metadata, new ColorConversionOptions());
        bool convert = targetNamed is not null || targetProfile is not null;
        bool wide = convert && targetNamed is not (null or NamedColorSpace.Srgb);

        bool carrier16 = format switch
        {
            "png" or "jxl" => Wants16(source, precision, wide),
            "avif" => (options?.RequestedBitDepth ?? 0) switch { > 8 => true, 8 => false, _ => Wants16(source, precision, wide) },
            _ => false,
        };
        var carrier = CarrierFormat(format, source, carrier16, precision, convert, options?.KeepsHalfFloat == true);
        bool carrier8 = !carrier.IsFloat() && carrier.GetBytesPerSample() == 1;

        // Linear data in an 8-bit carrier is stored as the matching encoded space: 8 bits of linear light band visibly.
        if (!convert && carrier8 && LinearCounterpart(metadata, sourceProfile, sourceSpace) is { } encoded)
        {
            targetNamed = encoded;
            convert = true;
        }

        if (!convert && carrier == source)
        {
            return new Plan(carrier, Passthrough: true, Convert: false, null, null, null, Unpremultiply: true);
        }

        ColorTransform? transform = null;
        if (convert)
        {
            transform = ColorTransform.Create(sourceProfile, sourceSpace, targetProfile, targetNamed ?? NamedColorSpace.Srgb, clip: true, intent: null);
        }

        // JPEG has no alpha: a premultiplied source already holds color composited over black, so it is stored as is rather than unpremultiplied.
        bool unpremultiply = !(format == "jpeg" && source.IsPremultiplied());
        return new Plan(carrier, Passthrough: false, convert, targetNamed, targetProfile, transform, unpremultiply);
    }

    public static Prepared Prepare(Image image, IImageCodec codec, EncoderOptions? options)
    {
        if (image.PixelFormat is PixelFormat.Cmyk32 or PixelFormat.Ycck32)
        {
            // Ink data is not something the preparation reinterprets: the encoder rejects it as it always has.
            return new Prepared(image, null);
        }

        bool wantsColor = options?.TargetColorSpace is not null || options?.TargetColorProfile is not null;
        if (!IsManaged(codec))
        {
            ThrowIfUnsupported(image, codec);
            return wantsColor ? ConvertForColorOnly(image, options!) : new Prepared(image, null);
        }

        var plan = MakePlan(image.PixelFormat, image.Metadata, codec, options);
        if (plan.Passthrough)
        {
            return new Prepared(image, null);
        }

        var result = ImagePixelConverter.ConvertWithTransform(image, plan.Carrier, ParallelSettings.From(options), plan.Transform is null ? null : plan.Transform.Apply, plan.Unpremultiply);
        try
        {
            image.CopyMetadataTo(result);
            result.HasAlpha = image.HasAlpha;
            if (plan.Convert)
            {
                ColorConverter.Retag(result.Metadata, plan.TargetNamed, plan.TargetProfile);
            }

            return new Prepared(result, result);
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prepares pixels that live in caller-owned memory without first building an <see cref="Image"/> of them: a managed encoder gets its carrier produced straight from
    /// the rows (strided, any format, converted in the same pass), so there is no intermediate copy; a tightly packed whole array whose format the encoder already
    /// takes is wrapped in place. <paramref name="metadata"/> is what the pixels declare (profiles, density). Exactly one of <paramref name="memory"/> and
    /// <paramref name="span"/> is the source, as <paramref name="useMemory"/> says.
    /// </summary>
    public static Prepared PrepareBuffer(ReadOnlyMemory<byte> memory, ReadOnlySpan<byte> span, bool useMemory, int width, int height, PixelFormat format, int strideBytes, ImageMetadata metadata, IImageCodec codec, EncoderOptions? options)
    {
        int rowBytes = checked(width * format.GetBytesPerPixel());
        int stride = strideBytes == 0 ? rowBytes : strideBytes;

        if (format is PixelFormat.Cmyk32 or PixelFormat.Ycck32 || !IsManaged(codec))
        {
            // A codec the preparation does not manage takes a plain image (and may apply a color request to it).
            var plain = useMemory ? Image.FromPixels(memory.Span, width, height, format, strideBytes) : Image.FromPixels(span, width, height, format, strideBytes);
            Carry(metadata, plain);
            var prepared = Prepare(plain, codec, options);
            return new Prepared(prepared.Image, ReferenceEquals(plain, prepared.Image) ? plain : prepared.Image);
        }

        var plan = MakePlan(format, metadata, codec, options);
        Image result;
        if (plan.Passthrough)
        {
            if (useMemory && stride == rowBytes && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) && segment.Offset == 0 && segment.Array is { } array)
            {
                result = Image.Wrap(array, width, height, format);
            }
            else
            {
                result = useMemory ? Image.FromPixels(memory.Span, width, height, format, strideBytes) : Image.FromPixels(span, width, height, format, strideBytes);
            }
        }
        else
        {
            ImagePixelConverter.RowTransform? transform = plan.Transform is null ? null : plan.Transform.Apply;
            result = useMemory
                ? ImagePixelConverter.ConvertRows(memory, stride, width, height, format, plan.Carrier, ParallelSettings.From(options), transform, plan.Unpremultiply)
                : ImagePixelConverter.ConvertRows(span, stride, width, height, format, plan.Carrier, transform, plan.Unpremultiply);
        }

        Carry(metadata, result);
        result.HasAlpha = format.HasAlpha();
        if (plan.Convert)
        {
            ColorConverter.Retag(result.Metadata, plan.TargetNamed, plan.TargetProfile);
        }

        return new Prepared(result, result);
    }

    private static void Carry(ImageMetadata metadata, Image target)
    {
        target.Metadata.HorizontalResolution = metadata.HorizontalResolution;
        target.Metadata.VerticalResolution = metadata.VerticalResolution;
        foreach (var profile in metadata.Profiles)
        {
            target.Metadata.Profiles.Add(profile);
        }
    }

    private static Prepared ConvertForColorOnly(Image image, EncoderOptions options)
    {
        var converted = ColorConverterEntry(image, options);
        return new Prepared(converted, converted);
    }

    private static Image ColorConverterEntry(Image image, EncoderOptions options)
    {
        var colorOptions = new ColorConversionOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, TaskScheduler = options.TaskScheduler };
        return options.TargetColorProfile is { } profile
            ? image.ConvertColorSpace(profile, colorOptions)
            : image.ConvertColorSpace(options.TargetColorSpace!.Value, colorOptions);
    }

    private static void ThrowIfUnsupported(Image image, IImageCodec codec)
    {
        if (image.PixelFormat.IsFloat() && !codec.CanEncodeFloatPixels)
        {
            throw new NotSupportedException($"The {codec.FormatName} encoder does not support {image.PixelFormat} pixels. Convert the image to an integer format first (Image.ConvertTo) to encode it.");
        }
    }

    private static bool Wants16(PixelFormat source, EncodePrecision precision, bool wideTarget) => precision switch
    {
        EncodePrecision.Eight => false,
        EncodePrecision.Sixteen => true,
        _ => source.GetBytesPerSample() > 1 || wideTarget,
    };

    private static PixelFormat CarrierFormat(string format, PixelFormat source, bool sixteen, EncodePrecision precision, bool convert, bool keepHalf)
    {
        bool gray = !convert && source is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32;
        bool alpha = source.HasAlpha();
        switch (format)
        {
            case "png":
                return gray ? (sixteen ? PixelFormat.Gray16 : PixelFormat.Gray8)
                    : alpha ? (sixteen ? PixelFormat.Rgba64 : PixelFormat.Rgba32)
                    : (sixteen ? PixelFormat.Rgb48 : PixelFormat.Rgb24);
            case "jpeg":
                return gray ? PixelFormat.Gray8 : source == PixelFormat.Rgba32 ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
            case "webp":
                return gray ? PixelFormat.Gray8 : alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
            case "avif":
                return gray ? (sixteen ? PixelFormat.Gray16 : PixelFormat.Gray8)
                    : alpha ? (sixteen ? PixelFormat.Rgba64 : PixelFormat.Rgba32)
                    : (sixteen ? PixelFormat.Rgb48 : PixelFormat.Rgb24);
            default: // jxl: keeps the sample type it can carry.
                if (precision == EncodePrecision.Eight && !source.IsFloat())
                {
                    return gray ? PixelFormat.Gray8 : alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
                }

                if (source.IsFloat() && (!source.IsHalfFloat() || keepHalf) && !source.IsPremultiplied())
                {
                    return source;
                }

                if (source.IsFloat())
                {
                    return gray ? PixelFormat.GrayF32 : alpha ? PixelFormat.RgbaF32 : PixelFormat.RgbF32;
                }

                if (sixteen || source.GetBytesPerSample() == 2)
                {
                    return gray ? PixelFormat.Gray16 : alpha ? PixelFormat.Rgba64 : PixelFormat.Rgb48;
                }

                return source.IsPremultiplied() ? PixelFormat.Rgba32 : source;
        }
    }

    private static NamedColorSpace? LinearCounterpart(ImageMetadata metadata, IccColorProfile? profile, NamedColorSpace assumed)
    {
        var named = profile?.Identify() ?? metadata.Cicp?.ToNamedColorSpace() ?? (profile is null ? assumed : null);
        return named switch
        {
            NamedColorSpace.LinearSrgb => NamedColorSpace.Srgb,
            NamedColorSpace.LinearDisplayP3 => NamedColorSpace.DisplayP3,
            NamedColorSpace.LinearRec2020 => NamedColorSpace.Rec2020,
            _ => null,
        };
    }
}
