using PeachImage.Formats.Avif.Container;
using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Internal;

namespace PeachImage.Formats.Avif;

/// <summary>
/// Decodes AVIF images: baseline still images (intra-only AV1, single item or a HEIF <c>grid</c>
/// composite), 8-bit and 10-bit, 4:2:0/4:2:2/4:4:4 and monochrome, with alpha via AVIF's auxiliary-item
/// mechanism. Animated AVIF (the <c>avis</c> brand), film grain synthesis, 12-bit depth, gain maps, and
/// palette/IntraBC mode are not supported and cause a clear <see cref="AvifUnsupportedFeatureException"/>
/// rather than a silently wrong decode. Used internally by <see cref="AvifCodec"/>.
/// </summary>
internal static class AvifDecoder
{
    private const string FormatName = "avif";

    public static bool IsSupportedFileFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12
            || header[4] != (byte)'f' || header[5] != (byte)'t' || header[6] != (byte)'y' || header[7] != (byte)'p')
        {
            return false;
        }

        if (IsAvifBrand(header.Slice(8, 4)))
        {
            return true;
        }

        // Some encoders set major_brand to something else (e.g. "mif1") and list "avif"/"avis" only
        // among the compatible brands (starting right after minor_version, at offset 16) -- scan every
        // brand slot that fits within the sniff window, not just major_brand.
        for (int offset = 16; offset + 4 <= header.Length; offset += 4)
        {
            if (IsAvifBrand(header.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var metadata = new ImageMetadata();
        var container = AvifContainerReader.Read(stream, metadata);
        var pixelFormat = AvifPixelFormatSelector.Choose(container.BitDepth, container.Monochrome, container.HasAlpha);
        return new ImageInfo(container.Width, container.Height, pixelFormat, FormatName);
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var metadata = new ImageMetadata();
        var container = AvifContainerReader.Read(stream, metadata);

        // In-loop filters (deblock/CDEF/loop restoration) are applied; animated AVIF, film grain
        // synthesis, gain maps, 12-bit depth, and palette/IntraBC mode remain out of scope and are
        // rejected earlier, during container/bitstream parsing, via AvifUnsupportedFeatureException.
        if (container.BitDepth is not (8 or 10))
        {
            throw new AvifUnsupportedFeatureException($"AVIF bit depth {container.BitDepth} is not supported.");
        }

        var color = Av1TileComposer.Composite(container.ColorTiles, container.GridRows, container.GridColumns, container.Width, container.Height);

        Av1TileComposer.CompositedFrame? alpha = null;
        int[]? alphaPlane = null;
        int alphaWidth = 0;
        if (container.AlphaTiles is { } alphaTiles)
        {
            alpha = Av1TileComposer.Composite(alphaTiles, container.GridRows, container.GridColumns, container.Width, container.Height);
            alphaPlane = alpha.Planes[0];
            alphaWidth = alpha.Widths[0];
        }

        var seq = color.Sequence;
        byte[] pixels = Av1YuvToRgbConverter.Convert(
            color.Planes, color.Widths, seq.MonoChrome, seq.SubsamplingX, seq.SubsamplingY,
            seq.MatrixCoefficients, seq.ColorRange, seq.BitDepth,
            alphaPlane, alphaWidth, container.Width, container.Height);

        // color.Planes/alpha.Planes (pool-rented by Av1TileComposer.Composite) have now been read for the
        // last time -- Convert() copies every sample it needs into the freshly-allocated `pixels` buffer.
        Av1TileComposer.ReturnPlanes(color);
        if (alpha is not null)
        {
            Av1TileComposer.ReturnPlanes(alpha);
        }

        var pixelFormat = AvifPixelFormatSelector.Choose(seq.BitDepth, seq.MonoChrome, alphaPlane is not null);
        var image = Image.FromBuffer(container.Width, container.Height, pixelFormat, pixels);

        foreach (var profile in metadata.Profiles)
        {
            image.Metadata.Profiles.Add(profile);
        }

        return Decoding.PixelFormatConverter.ConvertIfNeeded(image, options?.TargetPixelFormat);
    }

    private static bool IsAvifBrand(ReadOnlySpan<byte> brand) =>
        brand[0] == (byte)'a' && brand[1] == (byte)'v' && brand[2] == (byte)'i' && (brand[3] == (byte)'f' || brand[3] == (byte)'s');
}
