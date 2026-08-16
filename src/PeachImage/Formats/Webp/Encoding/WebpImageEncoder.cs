using PeachImage.Formats.Webp.Encoding.Vp8L;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding;

/// <summary>
/// Orchestrates a full WebP encode: gathers pixels into a flat ARGB buffer, hands them to
/// <see cref="Vp8LImageEncoder"/> for the VP8L chunk payload, and assembles the RIFF container --
/// simple format (bare <c>VP8L</c> chunk) when there's no metadata to carry, extended format
/// (<c>VP8X</c> + optional <c>ICCP</c>/<c>EXIF</c>/<c>XMP </c>) otherwise. No <c>ALPH</c> chunk is ever
/// written -- VP8L always carries alpha internally via its own <c>alpha_is_used</c> header bit.
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
            byte[] vp8LPayload = Vp8LImageEncoder.Encode(argb, pixelCount, image.Width, image.Height, hasAlpha, options);

            byte[]? icc = null;
            byte[]? exif = null;
            byte[]? xmp = null;
            if (options.IncludeMetadata)
            {
                (icc, exif, xmp) = WebpMetadataWriter.CollectProfiles(image.Metadata);
            }

            WriteContainer(stream, image.Width, image.Height, hasAlpha, vp8LPayload, icc, exif, xmp);
        }
        finally
        {
            WebpBufferPool.SharedUInt32.Return(argb);
        }
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

    private static void WriteContainer(Stream stream, int width, int height, bool hasAlpha, byte[] vp8LPayload, byte[]? icc, byte[]? exif, byte[]? xmp)
    {
        bool extended = icc is not null || exif is not null || xmp is not null;

        byte[]? vp8X = extended
            ? WebpContainerWriter.BuildVp8XPayload(width, height, hasAlpha, icc is not null, exif is not null, xmp is not null)
            : null;

        long riffSize = 4; // "WEBP" form type.
        riffSize += SizeIfPresent(vp8X);
        riffSize += SizeIfPresent(icc);
        riffSize += WebpContainerWriter.GetFramedChunkSize(vp8LPayload.Length);
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

        WebpContainerWriter.WriteChunk(stream, "VP8L", vp8LPayload);

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
