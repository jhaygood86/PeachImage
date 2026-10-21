using PeachImage.Formats.Jpeg2000.Decoding;
using PeachImage.Formats.Jpeg2000.Internal;
using PeachImage.Formats.Shared.IO;

namespace PeachImage.Formats.Jpeg2000;

/// <summary>Decodes JPEG 2000 (ITU-T T.800 Part 1) images: JP2 files and raw codestreams. Used internally by <see cref="Jpeg2000Codec"/>.</summary>
internal static class Jpeg2000Decoder
{
    private const string FormatName = "jpeg2000";

    /// <summary>Reads image dimensions and format information without decoding the tiles.</summary>
    public static ImageInfo Identify(Stream stream, DecoderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var jpeg2000 = options as Jpeg2000DecoderOptions;
        using var input = ReadInput(stream);
        try
        {
            var (jp2, codestream) = Open(input.ToMemory());
            var siz = codestream.Siz;
            var plan = J2kImagePlan.Create(siz, jp2, jpeg2000?.IgnoreColorSpace ?? false);
            int reduce = ClampReduce(codestream, jpeg2000?.ReduceResolutionLevels ?? 0);
            int width = CeilDivPow2(siz.Xsiz, reduce) - CeilDivPow2(siz.XOsiz, reduce);
            int height = CeilDivPow2(siz.Ysiz, reduce) - CeilDivPow2(siz.YOsiz, reduce);
            return new ImageInfo(width, height, plan.Format, FormatName, HasAlpha: plan.HasAlpha, IsLosslessEncoding: IsLossless(codestream));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 data is malformed.", ex);
        }
    }

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    public static Image Decode(Stream stream, DecoderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var jpeg2000 = options as Jpeg2000DecoderOptions;
        using var input = ReadInput(stream);
        try
        {
            var image = DecodeCore(input.ToMemory(), jpeg2000);
            if (options?.TargetPixelFormat is { } target && target != image.PixelFormat)
            {
                bool hasAlpha = image.HasAlpha;
                using (image)
                {
                    var converted = image.ConvertTo(target);
                    return converted;
                }
            }

            return image;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 data is malformed.", ex);
        }
    }

    private static PooledStreamBuffer ReadInput(Stream stream)
    {
        var input = PooledStreamBuffer.Read(stream, Jpeg2000DecodingLimits.MaxFileSize, out bool tooLarge);
        if (tooLarge)
        {
            input.Dispose();
            throw new Jpeg2000DecodingException("JPEG 2000 data exceeds the supported maximum size.");
        }

        return input;
    }

    private static (Jp2Info? Jp2, J2kCodestream Codestream) Open(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (Jp2Info.HasJp2Signature(span))
        {
            var jp2 = Jp2Info.Parse(data);
            return (jp2, J2kCodestream.Parse(jp2.Codestream));
        }

        if (Jp2Info.HasCodestreamSignature(span))
        {
            return (null, J2kCodestream.Parse(data));
        }

        throw new Jpeg2000DecodingException("Data is neither a JP2 file nor a JPEG 2000 codestream.");
    }

    private static Image DecodeCore(ReadOnlyMemory<byte> data, Jpeg2000DecoderOptions? options)
    {
        var (jp2, codestream) = Open(data);
        var siz = codestream.Siz;
        int reduce = ClampReduce(codestream, options?.ReduceResolutionLevels ?? 0);
        var plan = J2kImagePlan.Create(siz, jp2, options?.IgnoreColorSpace ?? false);

        int count = siz.Components.Length;
        var planes = new ushort[count][];
        var planeWidths = new int[count];
        var planeHeights = new int[count];
        var planeOriginX = new int[count];
        var planeOriginY = new int[count];
        for (int c = 0; c < count; c++)
        {
            var info = siz.Components[c];
            int x0 = CeilDivPow2(J2kSiz.CeilDiv(siz.XOsiz, info.XRsiz), reduce);
            int y0 = CeilDivPow2(J2kSiz.CeilDiv(siz.YOsiz, info.YRsiz), reduce);
            int x1 = CeilDivPow2(J2kSiz.CeilDiv(siz.Xsiz, info.XRsiz), reduce);
            int y1 = CeilDivPow2(J2kSiz.CeilDiv(siz.Ysiz, info.YRsiz), reduce);
            planeWidths[c] = Math.Max(1, x1 - x0);
            planeHeights[c] = Math.Max(1, y1 - y0);
            planeOriginX[c] = x0;
            planeOriginY[c] = y0;
            long pixels = (long)planeWidths[c] * planeHeights[c];
            if (pixels > Jpeg2000DecodingLimits.MaxPixelCount)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 image is too large.");
            }

            planes[c] = new ushort[pixels];
            if (!siz.Components[c].Signed)
            {
                // Missing tiles read as the DC-shifted zero value (mid-grey), like OpenJPEG.
            }
        }

        var tiles = codestream.TileParts.GroupBy(p => p.TileIndex).ToDictionary(g => g.Key, g => g.OrderBy(p => p.PartIndex).ToList());
        Parallel.ForEach(
            tiles,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            entry =>
            {
                var decoder = new J2kTileDecoder(codestream, entry.Key, reduce);
                try
                {
                    decoder.Decode(entry.Value);
                    J2kTileWriter.Write(decoder, siz, reduce, planes, planeWidths, planeOriginX, planeOriginY);
                }
                finally
                {
                    decoder.Release();
                }
            });

        int gridX0 = CeilDivPow2(siz.XOsiz, reduce);
        int gridY0 = CeilDivPow2(siz.YOsiz, reduce);
        int width = CeilDivPow2(siz.Xsiz, reduce) - gridX0;
        int height = CeilDivPow2(siz.Ysiz, reduce) - gridY0;
        var image = plan.Build(siz, reduce, planes, planeWidths, planeHeights, planeOriginX, planeOriginY, width, height, gridX0, gridY0);
        image.HasAlpha = plan.HasAlpha;
        if (jp2?.IccProfile is { } icc && !(options?.IgnoreColorSpace ?? false))
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });
        }

        return image;
    }

    /// <summary>The largest reduction every tile and component can honor.</summary>
    private static int ClampReduce(J2kCodestream codestream, int requested)
    {
        if (requested <= 0)
        {
            return 0;
        }

        int limit = int.MaxValue;
        void Consider(J2kHeaderSet set)
        {
            if (set.Cod is not null)
            {
                limit = Math.Min(limit, set.Cod.Coding.Levels);
            }

            foreach (var coc in set.Coc)
            {
                if (coc is not null)
                {
                    limit = Math.Min(limit, coc.Levels);
                }
            }
        }

        Consider(codestream.Main);
        foreach (var part in codestream.TileParts)
        {
            Consider(part.Header);
        }

        return Math.Min(requested, limit == int.MaxValue ? 0 : limit);
    }

    private static bool IsLossless(J2kCodestream codestream)
    {
        var main = codestream.Main;
        if (main.Cod is null || main.Qcd is null)
        {
            return false;
        }

        for (int c = 0; c < codestream.Siz.Components.Length; c++)
        {
            var coding = main.Coc[c] ?? main.Cod.Coding;
            var quant = main.Qcc[c] ?? main.Qcd;
            if (!coding.Reversible || quant.Style != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int CeilDivPow2(int value, int n) => (int)(((long)value + (1L << n) - 1) >> n);
}
