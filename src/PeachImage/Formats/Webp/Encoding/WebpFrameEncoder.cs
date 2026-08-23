using PeachImage.Formats.Webp.Encoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8L;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding;

/// <summary>
/// Encodes a single image's pixels into a <c>VP8L</c>/<c>VP8 </c> payload chunk -- the shared core of both
/// <see cref="WebpImageEncoder"/> (still images) and <see cref="WebpAnimationEncoder"/> (one call per
/// animation frame). Picks lossless VP8L (the default, and always for alpha-bearing images, since lossy
/// WebP has no alpha plane yet) or lossy VP8 per <see cref="WebpEncoderOptions.Lossless"/>.
/// </summary>
internal static class WebpFrameEncoder
{
    public static WebpFramePayload Encode(Image image, WebpEncoderOptions options)
    {
        if (image.Width > WebpDecodingLimits.MaxDimension || image.Height > WebpDecodingLimits.MaxDimension
            || (long)image.Width * image.Height > WebpDecodingLimits.MaxPixelCount)
        {
            throw new WebpEncodingException($"WebP image {image.Width}x{image.Height} exceeds the maximum supported dimensions.");
        }

        int pixelCount = image.Width * image.Height;
        uint[] argb = WebpBufferPool.SharedUInt32.Rent(pixelCount);
        try
        {
            bool hasAlpha = GatherArgb(image, argb);
            bool encodeLossy = !options.Lossless && !hasAlpha;

            byte[] payload;
            string fourCc;
            if (encodeLossy)
            {
                byte[] rgb = ExtractRgb(argb, pixelCount);
                payload = Vp8ImageEncoder.Encode(rgb, image.Width, image.Height, options);
                fourCc = "VP8 ";
            }
            else
            {
                payload = Vp8LImageEncoder.Encode(argb, pixelCount, image.Width, image.Height, hasAlpha, options);
                fourCc = "VP8L";
            }

            return new WebpFramePayload(fourCc, payload, hasAlpha);
        }
        finally
        {
            WebpBufferPool.SharedUInt32.Return(argb);
        }
    }

    /// <summary>Drops the alpha byte from a gathered <c>0xAARRGGBB</c> buffer into a fresh RGB24 buffer for <see cref="Vp8ImageEncoder"/>, which has no alpha plane to feed.</summary>
    private static byte[] ExtractRgb(uint[] argb, int pixelCount)
    {
        var rgb = new byte[pixelCount * 3];
        for (int i = 0; i < pixelCount; i++)
        {
            uint pixel = argb[i];
            int o = i * 3;
            rgb[o + 0] = (byte)(pixel >> 16);
            rgb[o + 1] = (byte)(pixel >> 8);
            rgb[o + 2] = (byte)pixel;
        }

        return rgb;
    }

    /// <summary>Packs <paramref name="image"/>'s pixels into <paramref name="destination"/> as <c>0xAARRGGBB</c> values. Returns whether the image needs a real alpha channel -- an <see cref="PixelFormat.Rgba32"/> source whose alpha is uniformly opaque is downgraded to <see langword="false"/>, matching cwebp's own default behavior of skipping a trivial alpha plane.</summary>
    private static bool GatherArgb(Image image, uint[] destination)
    {
        int width = image.Width;
        int height = image.Height;

        switch (image.PixelFormat)
        {
            case PixelFormat.Rgb24:
                for (int y = 0; y < height; y++)
                {
                    var row = image.GetRowSpan(y);
                    int rowBase = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int o = x * 3;
                        destination[rowBase + x] = 0xFF000000u | ((uint)row[o] << 16) | ((uint)row[o + 1] << 8) | row[o + 2];
                    }
                }

                return false;

            case PixelFormat.Rgba32:
                return GatherRgba32(image, destination, width, height);

            case PixelFormat.Gray8:
                for (int y = 0; y < height; y++)
                {
                    var row = image.GetRowSpan(y);
                    int rowBase = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        byte gray = row[x];
                        destination[rowBase + x] = 0xFF000000u | ((uint)gray << 16) | ((uint)gray << 8) | gray;
                    }
                }

                return false;

            default:
                throw new WebpEncodingException($"WebP encoding does not support {image.PixelFormat}; convert to Rgb24, Rgba32, or Gray8 first.");
        }
    }

    private static bool GatherRgba32(Image image, uint[] destination, int width, int height)
    {
        bool allOpaque = true;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                byte r = row[o];
                byte g = row[o + 1];
                byte b = row[o + 2];
                byte a = row[o + 3];
                allOpaque &= a == 0xFF;
                destination[rowBase + x] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
            }
        }

        return !allOpaque;
    }
}

/// <summary>The result of encoding one image's pixels into a WebP payload chunk: which bitstream it is, the encoded bytes, and whether it carries real alpha.</summary>
internal readonly record struct WebpFramePayload(string FourCc, byte[] Payload, bool HasAlpha);
