using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Container;
using PeachImage.Formats.Jxl.Encoder.Headers;
using PeachImage.Formats.Jxl.Encoder.Modular;
using PeachImage.Formats.Jxl.Encoder.VarDct;
using PeachImage.Formats.Jxl.Internal;
using PeachImage.Formats.Shared.Metadata;

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

        // A lossy file is coded in XYB, which assumes sRGB primaries, so pixels in another color space are converted
        // to sRGB first (the profile then no longer applies); a lossless file keeps its samples and carries the profile.
        if (icc is not null && !options.Lossless)
        {
            icc = null;
            if (image.PixelFormat is PixelFormat.Gray8 or PixelFormat.Rgb24)
            {
                image = image.ConvertToSrgb();
            }
        }

        var (input, bits, isGray, isFloat) = Gather(image);
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

        bool lossy = !options.Lossless;
        var info = new JxlEncodedImageInfo((uint)image.Width, (uint)image.Height, bits, isGray, hasAlpha, XybEncoded: lossy, orientation, icc, isFloat);
        var codestream = new JxlBitWriter(Math.Max(1024, image.Width * image.Height / 2));
        JxlCodestreamHeaderWriter.Write(codestream, info);
        if (lossy)
        {
            var lossyInput = new JxlVarDctFrameInput(input.Width, input.Height, bits, input.ColorChannels, input.Channels, isFloat);
            codestream.WriteBytes(JxlVarDctFrameEncoder.Encode(lossyInput, JxlVarDctQuality.Settings(options)));
        }
        else
        {
            codestream.WriteBytes(JxlModularFrameEncoder.Encode(input, xybEncoded: false));
        }

        JxlContainerWriter.Write(stream, codestream.AsSpan(), exif, xmp);
    }

    // The Exif profile may carry the JPEG-style "Exif\0\0" prefix; the container's Exif box wants the TIFF header first.
    private static byte[] StripExifPrefix(byte[] data) =>
        data.Length > 6 && data.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8) ? data[6..] : data;

    private static (JxlModularFrameInput Input, int Bits, bool IsGray, bool IsFloat) Gather(Image image)
    {
        int count = image.Width * image.Height;
        switch (image.PixelFormat)
        {
            case PixelFormat.Gray8:
                return (new JxlModularFrameInput(image.Width, image.Height, 1, [Plane8(image.GetPixelSpan(), 1, 0, count)]), 8, true, false);
            case PixelFormat.Rgb24:
            {
                var pixels = image.GetPixelSpan();
                return (new JxlModularFrameInput(image.Width, image.Height, 3, [Plane8(pixels, 3, 0, count), Plane8(pixels, 3, 1, count), Plane8(pixels, 3, 2, count)]), 8, false, false);
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

                return (new JxlModularFrameInput(image.Width, image.Height, 3, [.. planes]), 8, false, false);
            }

            case PixelFormat.Gray16:
                return (new JxlModularFrameInput(image.Width, image.Height, 1, [Plane16(image.GetPixelSpan(), 1, 0, count)]), 16, true, false);
            case PixelFormat.Rgb48:
            {
                var pixels = image.GetPixelSpan();
                return (new JxlModularFrameInput(image.Width, image.Height, 3, [Plane16(pixels, 3, 0, count), Plane16(pixels, 3, 1, count), Plane16(pixels, 3, 2, count)]), 16, false, false);
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

                return (new JxlModularFrameInput(image.Width, image.Height, 3, [.. planes]), 16, false, false);
            }

            // Float samples travel as their IEEE bit patterns: lossless codes the patterns exactly (the file's float bit depth tells
            // the decoder to reinterpret them), lossy reads them back as sRGB-encoded values.
            case PixelFormat.GrayF32:
                return (new JxlModularFrameInput(image.Width, image.Height, 1, [PlaneFloat(image.GetPixelSpan(), 1, 0, count)], colorTransform: false), 32, true, true);
            case PixelFormat.RgbF32:
            {
                var pixels = image.GetPixelSpan();
                return (new JxlModularFrameInput(image.Width, image.Height, 3, [PlaneFloat(pixels, 3, 0, count), PlaneFloat(pixels, 3, 1, count), PlaneFloat(pixels, 3, 2, count)], colorTransform: false), 32, false, true);
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

                return (new JxlModularFrameInput(image.Width, image.Height, 3, [.. planes], colorTransform: false), 32, false, true);
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
