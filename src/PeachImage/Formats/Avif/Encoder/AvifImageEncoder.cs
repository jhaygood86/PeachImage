using PeachImage.Formats.Avif.Container;
using PeachImage.Formats.Avif.Encoder.Av1;
using PeachImage.Formats.Avif.Internal;
using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Formats.Avif.Encoder;

/// <summary>
/// Orchestrates a full AVIF encode: validates the source image, converts it to the packed pixel buffer
/// <see cref="Av1FrameEncoder"/> expects, and hands the result to <see cref="AvifContainerWriter"/>. Used
/// internally by <see cref="AvifEncoder"/>.
/// </summary>
internal static class AvifImageEncoder
{
    public static void Encode(Image image, Stream stream, AvifEncoderOptions options)
    {
        if (image.Width > AvifDecodingLimits.MaxDimension || image.Height > AvifDecodingLimits.MaxDimension
            || (long)image.Width * image.Height > AvifDecodingLimits.MaxPixelCount)
        {
            throw new AvifEncodingException($"AVIF image {image.Width}x{image.Height} exceeds the maximum supported dimensions.");
        }

        // The color description the file declares (PNG-style cICP carried in the image metadata), signalled in both the AV1 sequence header
        // and an nclx colr box so the two agree. Nothing is written when the image declares none, which keeps untagged encodes as they were.
        var cicp = options.WritesIccProfile || options.IncludeMetadata ? image.Metadata.Cicp : null;
        int primaries = cicp?.ColorPrimaries ?? Av1SequenceHeaderWriter.ColorPrimaries;
        int transfer = cicp?.TransferCharacteristics ?? Av1SequenceHeaderWriter.TransferCharacteristics;

        Av1EncodedFrame encoded;
        Av1EncodedFrame? alphaEncoded = null;
        int bitDepth = ResolveBitDepth(image.PixelFormat, options.BitDepth);
        if (bitDepth == 8)
        {
            (byte[] pixels, bool monoChrome, byte[]? alpha) = GatherPixels(image);

            encoded = Av1FrameEncoder.Encode(pixels, image.Width, image.Height, monoChrome, options.Quality, options.Lossless, options.Effort, tileRowsLog2: options.TileRowsLog2, maxDegreeOfParallelism: options.MaxDegreeOfParallelism, scheduler: options.TaskScheduler, colorPrimaries: primaries, transferCharacteristics: transfer);

            // Alpha is encoded as its own independent monochrome AV1 image -- Av1FrameEncoder's existing
            // monoChrome path already produces exactly that, so no encoder-internal alpha support is needed,
            // only a second Encode call and container-level muxing (see AvifContainerWriter's remarks). Alpha is
            // lossless whenever the color plane is, so a Lossless request never silently loses transparency.
            if (alpha is not null)
            {
                alphaEncoded = Av1FrameEncoder.Encode(alpha, image.Width, image.Height, monoChrome: true, options.Quality, options.Lossless, options.Effort, tileRowsLog2: options.TileRowsLog2, maxDegreeOfParallelism: options.MaxDegreeOfParallelism, scheduler: options.TaskScheduler);
            }
        }
        else
        {
            (ushort[] samples, bool monoChrome, ushort[]? alpha) = GatherHighDepth(image, bitDepth);
            encoded = Av1FrameEncoder.EncodeHighDepth(samples, image.Width, image.Height, monoChrome, options.Quality, bitDepth, options.Lossless, options.Effort, options.TileRowsLog2, options.MaxDegreeOfParallelism, options.TaskScheduler, primaries, transfer);
            if (alpha is not null)
            {
                alphaEncoded = Av1FrameEncoder.EncodeHighDepth(alpha, image.Width, image.Height, monoChrome: true, options.Quality, bitDepth, options.Lossless, options.Effort, options.TileRowsLog2, options.MaxDegreeOfParallelism, options.TaskScheduler);
            }
        }

        byte[]? exif = EncodableMetadata.Exif(image, options);
        var orientation = exif is null ? ImageOrientation.Normal : ExifOrientationReader.ReadTiff(exif);
        if (cicp is not null)
        {
            encoded = encoded with { ColorSignal = (primaries, transfer) };
        }

        AvifContainerWriter.Write(stream, encoded, alphaEncoded, EncodableMetadata.Icc(image, options), exif, orientation, EncodableMetadata.Xmp(image, options));
    }

    /// <summary>8 for 8-bit sources; for 16-bit and float ones the requested depth, defaulting to 10. An explicit depth always wins.</summary>
    private static int ResolveBitDepth(PixelFormat format, int requested)
    {
        bool eightBit = format is PixelFormat.Rgb24 or PixelFormat.Gray8 or PixelFormat.Rgba32;
        if (requested != 0)
        {
            return requested;
        }

        return eightBit ? 8 : 10;
    }

    /// <summary>
    /// Validates <paramref name="image"/>'s pixel format and returns a packed RGB24 (or, for
    /// <see cref="PixelFormat.Gray8"/>, Gray8) buffer <see cref="Av1FrameEncoder"/> can consume directly,
    /// plus a separate Gray8 alpha plane if <paramref name="image"/> is <see cref="PixelFormat.Rgba32"/> with
    /// any non-opaque pixel (<see langword="null"/> otherwise -- including for a fully-opaque Rgba32 source,
    /// which is auto-downgraded to plain RGB24 with no alpha item at all, matching the existing WebP
    /// encoder's precedent in this repo and avoiding an unnecessary second AV1 image for the common case).
    /// A 16-bit source encoded at 8 bits is rounded to nearest.
    /// </summary>
    private static (byte[] Pixels, bool MonoChrome, byte[]? Alpha) GatherPixels(Image image)
    {
        switch (image.PixelFormat)
        {
            case PixelFormat.Rgb24:
                return (image.GetPixelSpan().ToArray(), false, null);

            case PixelFormat.Gray8:
                return (image.GetPixelSpan().ToArray(), true, null);

            case PixelFormat.Rgba32:
                return GatherRgbAndAlphaFromRgba32(image);

            case PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64:
            {
                var (samples, mono, alpha) = GatherHighDepth(image, 8);
                var bytes = new byte[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    bytes[i] = (byte)samples[i];
                }

                byte[]? alphaBytes = null;
                if (alpha is not null)
                {
                    alphaBytes = new byte[alpha.Length];
                    for (int i = 0; i < alpha.Length; i++)
                    {
                        alphaBytes[i] = (byte)alpha[i];
                    }
                }

                return (bytes, mono, alphaBytes);
            }

            default:
                throw new AvifEncodingException($"AVIF encoding does not support {image.PixelFormat}; convert to Rgb24, Rgba32, Gray8, Gray16, Rgb48 or Rgba64 first.");
        }
    }

    /// <summary>Packs a 16-bit source into samples of <paramref name="bitDepth"/> bits (rounded to nearest) and an alpha plane when any pixel is not opaque.</summary>
    private static (ushort[] Samples, bool MonoChrome, ushort[]? Alpha) GatherHighDepth(Image image, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        int pixelCount = image.Width * image.Height;
        var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());

        switch (image.PixelFormat)
        {
            case PixelFormat.Gray16:
            {
                var gray = new ushort[pixelCount];
                for (int i = 0; i < pixelCount; i++)
                {
                    gray[i] = Scale(source[i], max);
                }

                return (gray, true, null);
            }

            case PixelFormat.Rgb48:
            {
                var rgb = new ushort[pixelCount * 3];
                for (int i = 0; i < rgb.Length; i++)
                {
                    rgb[i] = Scale(source[i], max);
                }

                return (rgb, false, null);
            }

            case PixelFormat.Rgba64:
            {
                var rgb = new ushort[pixelCount * 3];
                var alpha = new ushort[pixelCount];
                bool transparent = false;
                for (int i = 0; i < pixelCount; i++)
                {
                    rgb[i * 3] = Scale(source[i * 4], max);
                    rgb[(i * 3) + 1] = Scale(source[(i * 4) + 1], max);
                    rgb[(i * 3) + 2] = Scale(source[(i * 4) + 2], max);
                    alpha[i] = Scale(source[(i * 4) + 3], max);
                    transparent |= alpha[i] != max;
                }

                return (rgb, false, transparent ? alpha : null);
            }

            default:
                // An 8-bit source asked to be encoded at a higher depth: widen proportionally.
                return GatherWidened(image, max);
        }
    }

    private static (ushort[] Samples, bool MonoChrome, ushort[]? Alpha) GatherWidened(Image image, int max)
    {
        var (bytes, mono, alpha) = GatherPixels(image);
        var samples = new ushort[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            samples[i] = (ushort)(((bytes[i] * max) + 127) / 255);
        }

        ushort[]? alphaSamples = null;
        if (alpha is not null)
        {
            alphaSamples = new ushort[alpha.Length];
            for (int i = 0; i < alpha.Length; i++)
            {
                alphaSamples[i] = (ushort)(((alpha[i] * max) + 127) / 255);
            }
        }

        return (samples, mono, alphaSamples);
    }

    private static ushort Scale(ushort value, int max) => (ushort)(((uint)value * (uint)max + 32767u) / 65535u);

    private static (byte[] Pixels, bool MonoChrome, byte[]? Alpha) GatherRgbAndAlphaFromRgba32(Image image)
    {
        var rgba = image.GetPixelSpan();
        int pixelCount = image.Width * image.Height;
        var rgb = new byte[pixelCount * 3];
        var alpha = new byte[pixelCount];
        bool hasTransparency = false;

        for (int i = 0; i < pixelCount; i++)
        {
            int srcIdx = i * 4;
            int dstIdx = i * 3;
            rgb[dstIdx] = rgba[srcIdx];
            rgb[dstIdx + 1] = rgba[srcIdx + 1];
            rgb[dstIdx + 2] = rgba[srcIdx + 2];

            byte a = rgba[srcIdx + 3];
            alpha[i] = a;
            hasTransparency |= a != 0xFF;
        }

        return (rgb, false, hasTransparency ? alpha : null);
    }
}
