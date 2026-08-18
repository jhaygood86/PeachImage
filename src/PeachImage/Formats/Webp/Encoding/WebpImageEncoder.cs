using PeachImage.Formats.Webp.Encoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8L;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding;

/// <summary>
/// Orchestrates a full WebP encode: gathers pixels into a flat ARGB buffer, hands them to either
/// <see cref="Vp8LImageEncoder"/> (lossless, the default) or <see cref="Vp8ImageEncoder"/> (lossy, when
/// <see cref="WebpEncoderOptions.Lossless"/> is <see langword="false"/> and the image needs no real alpha
/// channel) for the payload chunk, and assembles the RIFF container -- simple format (a bare <c>VP8L</c>/
/// <c>VP8 </c> chunk) when there's no metadata to carry, extended format (<c>VP8X</c> + optional
/// <c>ICCP</c>/<c>EXIF</c>/<c>XMP </c>) otherwise. No <c>ALPH</c> chunk is ever written: VP8L always carries
/// alpha internally via its own <c>alpha_is_used</c> header bit, and VP8 lossy encode is never selected for an
/// alpha-bearing image (see <see cref="WebpEncoderOptions.Lossless"/>'s remarks) until a follow-on adds it.
/// </summary>
internal static class WebpImageEncoder
{
    public static void Encode(Image image, Stream stream, WebpEncoderOptions options)
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

            byte[]? icc = null;
            byte[]? exif = null;
            byte[]? xmp = null;
            if (options.IncludeMetadata)
            {
                (icc, exif, xmp) = WebpMetadataWriter.CollectProfiles(image.Metadata);
            }

            WriteContainer(stream, image.Width, image.Height, hasAlpha, fourCc, payload, icc, exif, xmp);
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

    /// <summary>
    /// Writes the RIFF container around whichever payload chunk was produced -- <c>VP8L</c> or <c>VP8 </c>
    /// (<paramref name="fourCc"/>/<paramref name="payload"/>). The container format itself doesn't distinguish
    /// lossy from lossless (there is no "is this lossless" bit in the <c>VP8X</c> flags byte -- a decoder tells
    /// the two apart purely by which of the two chunk FourCCs is present), so this needed no changes beyond
    /// parameterizing which chunk gets written.
    /// </summary>
    private static void WriteContainer(Stream stream, int width, int height, bool hasAlpha, string fourCc, byte[] payload, byte[]? icc, byte[]? exif, byte[]? xmp)
    {
        bool extended = icc is not null || exif is not null || xmp is not null;

        byte[]? vp8X = extended
            ? WebpContainerWriter.BuildVp8XPayload(width, height, hasAlpha, icc is not null, exif is not null, xmp is not null)
            : null;

        long riffSize = 4; // "WEBP" form type.
        riffSize += SizeIfPresent(vp8X);
        riffSize += SizeIfPresent(icc);
        riffSize += WebpContainerWriter.GetFramedChunkSize(payload.Length);
        riffSize += SizeIfPresent(exif);
        riffSize += SizeIfPresent(xmp);

        WebpContainerWriter.WriteRiffHeader(stream, (uint)riffSize);

        if (vp8X is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "VP8X", vp8X);
        }

        if (icc is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "ICCP", icc);
        }

        WebpContainerWriter.WriteChunk(stream, fourCc, payload);

        if (exif is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "EXIF", exif);
        }

        if (xmp is not null)
        {
            WebpContainerWriter.WriteChunk(stream, "XMP ", xmp);
        }
    }

    private static long SizeIfPresent(byte[]? payload) => payload is null ? 0 : WebpContainerWriter.GetFramedChunkSize(payload.Length);
}
