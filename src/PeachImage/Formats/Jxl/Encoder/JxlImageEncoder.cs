using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Container;
using PeachImage.Formats.Jxl.Encoder.Headers;
using PeachImage.Formats.Jxl.Encoder.Modular;
using PeachImage.Formats.Jxl.Encoder.VarDct;
using PeachImage.Formats.Jxl.Internal;
using PeachImage.Formats.Shared.Metadata;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Encoder;

/// <summary>Turns an <see cref="Image"/> into a JPEG XL file: validates and unpacks the pixels, codes the frame and wraps it with metadata.</summary>
internal static class JxlImageEncoder
{
    public static void Encode(Image image, Stream stream, JxlEncoderOptions options)
    {
        if ((long)image.Width * image.Height > JxlDecodingLimits.MaxPixelCount)
        {
            throw new JxlEncodingException("The image is too large for JPEG XL encoding.");
        }

        if (options.Quality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Quality must be between 0 and 100.");
        }

        if (options.Effort is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Effort must be between 1 and 9.");
        }

        byte[]? icc = null;
        if (options.WritesIccProfile)
        {
            foreach (var profile in image.Metadata.Profiles)
            {
                if (profile.Kind == MetadataProfileKind.Icc)
                {
                    icc = profile.Data;
                    break;
                }
            }
        }

        // The space the pixels are declared to be in: an ICC profile that is one of the named spaces, or cICP code points, can be written as
        // enums (no profile bytes, and a decoder with no ICC engine understands it). Any other profile is carried as ICC.
        NamedColorSpace? declared = null;
        if (options.WritesIccProfile || options.IncludeMetadata)
        {
            if (icc is not null && IccColorProfile.TryCreate(icc, out var parsed) && parsed!.Identify() is { } identified)
            {
                declared = identified;
            }
            else if (icc is null)
            {
                declared = image.Metadata.Cicp?.ToNamedColorSpace();
            }
        }

        bool lossy = !options.Lossless;
        if (image.PixelFormat == PixelFormat.RgbaF16 && (lossy || !options.KeepsHalfFloat))
        {
            image = image.ConvertTo(PixelFormat.RgbaF32);
        }

        NamedColorSpace? signalled = null;
        NamedColorSpace? wideDeclared = null;
        if (lossy)
        {
            // A lossy file is coded in XYB, which assumes sRGB primaries, so pixels in another color space are converted to sRGB first (the
            // profile then no longer applies); linear sRGB float data is converted to sRGB-encoded values and declared linear, so a decoder
            // gives back linear light. A lossless file keeps its samples and declares the space they are in.
            // Gray carries a transfer function but no primaries, so every declared space except sRGB goes this way; for color, linear sRGB keeps the simpler conversion below.
            bool grayInput = IsGray(image.PixelFormat);
            bool wideOrHdr = grayInput ? declared is not (null or NamedColorSpace.Srgb) : declared is not (null or NamedColorSpace.Srgb or NamedColorSpace.LinearSrgb);
            if (wideOrHdr)
            {
                // Wide gamut and HDR keep their space: the file declares it, and the colors are handed to XYB as sRGB-primaries light (see
                // WideGamutPlanes), which the decoder converts back to the declared primaries and transfer function.
                wideDeclared = declared;
                signalled = declared;
                icc = null;
            }
            else if (icc is not null && image.PixelFormat is PixelFormat.Gray8 or PixelFormat.Rgb24)
            {
                image = image.ConvertToSrgb();
                icc = null;
            }
            else if (!IsGray(image.PixelFormat) && (icc is not null || declared is not (null or NamedColorSpace.Srgb)))
            {
                bool floating = image.PixelFormat.IsFloat();
                image = image.ConvertColorSpace(NamedColorSpace.Srgb, new ColorConversionOptions { GamutHandling = floating ? GamutHandling.Preserve : GamutHandling.Clip });
                signalled = floating && declared == NamedColorSpace.LinearSrgb ? NamedColorSpace.LinearSrgb : null;
                icc = null;
            }
            else
            {
                icc = null;
            }
        }
        else if (declared is not null)
        {
            signalled = declared;
            icc = null;
        }

        var (input, bits, isGray, isFloat, isHalf) = Gather(image);
        if (wideDeclared is { } wide)
        {
            input = new JxlModularFrameInput(input.Width, input.Height, input.ColorChannels, WideGamutPlanes(image, input, wide), colorTransform: false);
        }

        bool hasAlpha = input.Channels.Length > input.ColorChannels;

        byte[]? exif = null;
        byte[]? xmp = null;
        int orientation = 1;
        if (options.IncludeMetadata)
        {
            foreach (var profile in image.Metadata.Profiles)
            {
                if (profile.Kind == MetadataProfileKind.Exif && exif is null)
                {
                    exif = StripExifPrefix(profile.Data);
                    orientation = (int)ExifOrientationReader.ReadTiff(exif);
                }
                else if (profile.Kind == MetadataProfileKind.Xmp)
                {
                    xmp ??= profile.Data;
                }
            }
        }

        var info = new JxlEncodedImageInfo((uint)image.Width, (uint)image.Height, bits, isGray, hasAlpha, XybEncoded: lossy, orientation, icc, isFloat, isHalf, signalled, IntensityTargetFor(declared));
        var codestream = new JxlBitWriter(Math.Max(1024, image.Width * image.Height / 2));
        JxlCodestreamHeaderWriter.Write(codestream, info);
        if (lossy)
        {
            var lossyInput = new JxlVarDctFrameInput(input.Width, input.Height, bits, input.ColorChannels, input.Channels, isFloat || wideDeclared is not null);
            codestream.WriteBytes(JxlVarDctFrameEncoder.Encode(lossyInput, JxlVarDctQuality.Settings(options), ParallelSettings.From(options)));
        }
        else
        {
            codestream.WriteBytes(JxlModularFrameEncoder.Encode(input, xybEncoded: false, ParallelSettings.From(options)));
        }

        JxlContainerWriter.Write(stream, codestream.AsSpan(), exif, xmp);
    }

    // The luminance, in cd/m2, a decoder is told the content peaks at (the tone mapping header's intensity target): PQ spans the whole
    // 10,000 cd/m2 range, HLG is defined for a 1,000 cd/m2 display. SDR spaces keep the default.
    private static float? IntensityTargetFor(NamedColorSpace? space) => space switch
    {
        NamedColorSpace.Rec2100Pq => 10000f,
        NamedColorSpace.Rec2100Hlg => 1000f,
        _ => null,
    };

    /// <summary>
    /// The three color planes a lossy file needs for pixels in <paramref name="space"/>: the colors as linear light in sRGB primaries (negative or above 1
    /// for what lies outside sRGB or above SDR white), scaled so 1.0 is 255 cd/m2 the way XYB is defined, then sRGB-encoded (odd-symmetric) because the
    /// XYB step linearizes with that curve. A decoder undoes the transform and converts to the declared primaries and transfer function, so wide-gamut
    /// and HDR values survive the lossy coding. They are float samples stored as their bit patterns; extra channels (alpha) are kept as they were.
    /// </summary>
    private static int[][] WideGamutPlanes(Image image, JxlModularFrameInput input, NamedColorSpace space)
    {
        // A gray image is taken as neutral color in the declared space; only its luminance plane is kept.
        bool gray = input.ColorChannels == 1;
        using var expanded = gray ? image.ConvertTo(PixelFormat.RgbF32) : null;
        using var linear = (expanded ?? image).ConvertColorSpace(NamedColorSpace.LinearSrgb, new ColorConversionOptions
        {
            GamutHandling = GamutHandling.Preserve,
            TargetPixelFormat = PixelFormat.RgbF32,
            HdrToneMapping = HdrToneMapping.Preserve,
        });

        int count = image.Width * image.Height;
        int colorPlanes = input.ColorChannels;
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(linear.GetPixelSpan());
        var planes = new float[colorPlanes][];
        for (int c = 0; c < colorPlanes; c++)
        {
            planes[c] = new float[count];
            for (int i = 0; i < count; i++)
            {
                planes[c][i] = samples[(i * 3) + c];
            }
        }

        // Linear values are in units of SDR reference white (203 cd/m2) for HDR spaces; XYB's unit is 255 cd/m2.
        bool hdr = space is NamedColorSpace.Rec2100Pq or NamedColorSpace.Rec2100Hlg;
        float scale = hdr ? 203f / 255f : 1f;
        var channels = new int[input.Channels.Length][];
        for (int c = 0; c < colorPlanes; c++)
        {
            if (scale != 1f)
            {
                for (int i = 0; i < count; i++)
                {
                    planes[c][i] *= scale;
                }
            }

            PeachImage.Internal.Color.ColorKernels.FromLinear(TransferFunction.Srgb, 0, planes[c]);
            channels[c] = new int[count];
            for (int i = 0; i < count; i++)
            {
                channels[c][i] = BitConverter.SingleToInt32Bits(planes[c][i]);
            }
        }

        for (int c = colorPlanes; c < channels.Length; c++)
        {
            channels[c] = input.Channels[c];
        }

        return channels;
    }

    // The Exif profile may carry the JPEG-style "Exif\0\0" prefix; the container's Exif box wants the TIFF header first.
    private static byte[] StripExifPrefix(byte[] data) =>
        data.Length > 6 && data.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8) ? data[6..] : data;

    private static bool IsGray(PixelFormat format) => format is PixelFormat.Gray8 or PixelFormat.Gray16 or PixelFormat.GrayF32;

    private static (JxlModularFrameInput Input, int Bits, bool IsGray, bool IsFloat, bool IsHalf) Gather(Image image)
    {
        int count = image.Width * image.Height;
        switch (image.PixelFormat)
        {
            case PixelFormat.Gray8:
                return (new JxlModularFrameInput(image.Width, image.Height, 1, [Plane8(image.GetPixelSpan(), 1, 0, count)]), 8, true, false, false);
            case PixelFormat.Rgb24:
            {
                var pixels = image.GetPixelSpan();
                return (new JxlModularFrameInput(image.Width, image.Height, 3, [Plane8(pixels, 3, 0, count), Plane8(pixels, 3, 1, count), Plane8(pixels, 3, 2, count)]), 8, false, false, false);
            }

            case PixelFormat.Rgba32:
            {
                var pixels = image.GetPixelSpan();
                var planes = new List<int[]> { Plane8(pixels, 4, 0, count), Plane8(pixels, 4, 1, count), Plane8(pixels, 4, 2, count) };
                var alpha = Plane8(pixels, 4, 3, count);
                if (!IsOpaque(alpha, 255))
                {
                    planes.Add(alpha);
                }

                return (new JxlModularFrameInput(image.Width, image.Height, 3, [.. planes]), 8, false, false, false);
            }

            case PixelFormat.Gray16:
                return (new JxlModularFrameInput(image.Width, image.Height, 1, [Plane16(image.GetPixelSpan(), 1, 0, count)]), 16, true, false, false);
            case PixelFormat.Rgb48:
            {
                var pixels = image.GetPixelSpan();
                return (new JxlModularFrameInput(image.Width, image.Height, 3, [Plane16(pixels, 3, 0, count), Plane16(pixels, 3, 1, count), Plane16(pixels, 3, 2, count)]), 16, false, false, false);
            }

            case PixelFormat.Rgba64:
            {
                var pixels = image.GetPixelSpan();
                var planes = new List<int[]> { Plane16(pixels, 4, 0, count), Plane16(pixels, 4, 1, count), Plane16(pixels, 4, 2, count) };
                var alpha = Plane16(pixels, 4, 3, count);
                if (!IsOpaque(alpha, 65535))
                {
                    planes.Add(alpha);
                }

                return (new JxlModularFrameInput(image.Width, image.Height, 3, [.. planes]), 16, false, false, false);
            }

            // Float samples travel as their IEEE bit patterns: lossless codes the patterns exactly (the file's float bit depth tells
            // the decoder to reinterpret them), lossy reads them back as sRGB-encoded values.
            case PixelFormat.GrayF32:
                return (new JxlModularFrameInput(image.Width, image.Height, 1, [PlaneFloat(image.GetPixelSpan(), 1, 0, count)], colorTransform: false), 32, true, true, false);
            case PixelFormat.RgbF32:
            {
                var pixels = image.GetPixelSpan();
                return (new JxlModularFrameInput(image.Width, image.Height, 3, [PlaneFloat(pixels, 3, 0, count), PlaneFloat(pixels, 3, 1, count), PlaneFloat(pixels, 3, 2, count)], colorTransform: false), 32, false, true, false);
            }

            case PixelFormat.RgbaF32:
            {
                var pixels = image.GetPixelSpan();
                var planes = new List<int[]> { PlaneFloat(pixels, 4, 0, count), PlaneFloat(pixels, 4, 1, count), PlaneFloat(pixels, 4, 2, count) };
                var alpha = PlaneFloat(pixels, 4, 3, count);
                if (!IsOpaque(alpha, BitConverter.SingleToInt32Bits(1f)))
                {
                    planes.Add(alpha);
                }

                return (new JxlModularFrameInput(image.Width, image.Height, 3, [.. planes], colorTransform: false), 32, false, true, false);
            }

            // Half-float samples travel as their binary16 bit patterns in a 16-bit float file: lossless, and a quarter smaller than widening to 32 bits.
            case PixelFormat.RgbaF16:
            {
                var pixels = image.GetPixelSpan();
                var planes = new List<int[]> { Plane16(pixels, 4, 0, count), Plane16(pixels, 4, 1, count), Plane16(pixels, 4, 2, count) };
                var alpha = Plane16(pixels, 4, 3, count);
                if (!IsOpaque(alpha, BitConverter.HalfToUInt16Bits((Half)1f)))
                {
                    planes.Add(alpha);
                }

                return (new JxlModularFrameInput(image.Width, image.Height, 3, [.. planes], colorTransform: false), 16, false, true, true);
            }

            default:
                throw new JxlEncodingException($"JPEG XL encoding does not support {image.PixelFormat}; convert to Gray8, Rgb24, Rgba32, their 16-bit forms or the float formats first.");
        }
    }

    private static bool IsOpaque(int[] alpha, int opaque)
    {
        foreach (int a in alpha)
        {
            if (a != opaque)
            {
                return false;
            }
        }

        return true;
    }

    private static int[] Plane8(ReadOnlySpan<byte> pixels, int stride, int offset, int count)
    {
        int[] plane = new int[count];
        for (int i = 0; i < count; i++)
        {
            plane[i] = pixels[(i * stride) + offset];
        }

        return plane;
    }

    private static int[] PlaneFloat(ReadOnlySpan<byte> pixels, int stride, int offset, int count)
    {
        var samples = MemoryMarshal.Cast<byte, int>(pixels);
        int[] plane = new int[count];
        for (int i = 0; i < count; i++)
        {
            plane[i] = samples[(i * stride) + offset];
        }

        return plane;
    }

    private static int[] Plane16(ReadOnlySpan<byte> pixels, int stride, int offset, int count)
    {
        var samples = MemoryMarshal.Cast<byte, ushort>(pixels);
        int[] plane = new int[count];
        for (int i = 0; i < count; i++)
        {
            plane[i] = samples[(i * stride) + offset];
        }

        return plane;
    }
}
