using PeachImage.Formats.Jpeg2000.Internal;

namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>One image component's declaration in the SIZ marker segment.</summary>
internal readonly record struct J2kComponentInfo(int Depth, bool Signed, int XRsiz, int YRsiz);

/// <summary>The SIZ marker segment: the reference grid, tile grid and components.</summary>
internal sealed class J2kSiz
{
    public int Xsiz { get; init; }

    public int Ysiz { get; init; }

    public int XOsiz { get; init; }

    public int YOsiz { get; init; }

    public int XTsiz { get; init; }

    public int YTsiz { get; init; }

    public int XTOsiz { get; init; }

    public int YTOsiz { get; init; }

    public required J2kComponentInfo[] Components { get; init; }

    public int TilesWide => CeilDiv(Xsiz - XTOsiz, XTsiz);

    public int TilesHigh => CeilDiv(Ysiz - YTOsiz, YTsiz);

    public static int CeilDiv(int a, int b) => (int)(((long)a + b - 1) / b);
}

/// <summary>The per-component coding style fields shared by the COD and COC marker segments.</summary>
internal sealed class J2kComponentCoding
{
    public int Levels { get; init; }

    /// <summary>Code-block width exponent (already offset by 2).</summary>
    public int CodeBlockWidthExponent { get; init; }

    public int CodeBlockHeightExponent { get; init; }

    public int CodeBlockStyle { get; init; }

    public bool Reversible { get; init; }

    /// <summary>Per resolution, the precinct size exponents (low nibble PPx, high nibble PPy); null for the default 2^15.</summary>
    public byte[]? PrecinctSizes { get; init; }

    public int PrecinctWidthExponent(int resolution) => PrecinctSizes is null ? 15 : PrecinctSizes[resolution] & 0xF;

    public int PrecinctHeightExponent(int resolution) => PrecinctSizes is null ? 15 : PrecinctSizes[resolution] >> 4;
}

/// <summary>The COD marker segment.</summary>
internal sealed class J2kCod
{
    public bool Sop { get; init; }

    public bool Eph { get; init; }

    public int Progression { get; init; }

    public int Layers { get; init; }

    public bool Mct { get; init; }

    public required J2kComponentCoding Coding { get; init; }
}

/// <summary>The QCD/QCC marker segments: quantization style, guard bits and per-subband exponent/mantissa values.</summary>
internal sealed class J2kQuantization
{
    /// <summary>0 none, 1 scalar derived, 2 scalar expounded.</summary>
    public int Style { get; init; }

    public int GuardBits { get; init; }

    public required int[] Exponents { get; init; }

    public required int[] Mantissas { get; init; }
}

/// <summary>One progression order change.</summary>
internal readonly record struct J2kProgressionChange(int ResolutionStart, int ComponentStart, int LayerEnd, int ResolutionEnd, int ComponentEnd, int Order);

/// <summary>The marker segments that may appear in a main header or a tile-part header, kept apart so tile values can override main values.</summary>
internal sealed class J2kHeaderSet
{
    public J2kHeaderSet(int componentCount)
    {
        Coc = new J2kComponentCoding?[componentCount];
        Qcc = new J2kQuantization?[componentCount];
        Rgn = new int?[componentCount];
    }

    public J2kCod? Cod { get; set; }

    public J2kComponentCoding?[] Coc { get; }

    public J2kQuantization? Qcd { get; set; }

    public J2kQuantization?[] Qcc { get; }

    public int?[] Rgn { get; }

    public List<J2kProgressionChange>? Poc { get; set; }
}

/// <summary>A tile-part: its tile, its own header markers and where its packet data lies.</summary>
internal sealed class J2kTilePart
{
    public int TileIndex { get; init; }

    public int PartIndex { get; init; }

    public required J2kHeaderSet Header { get; init; }

    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Packed packet headers from a PPM marker for this tile-part, if any.</summary>
    public ReadOnlyMemory<byte> PpmHeaders { get; set; }

    /// <summary>Packed packet headers from PPT markers in this tile-part's header, in marker order.</summary>
    public List<(int Index, byte[] Data)> PptSegments { get; } = [];
}

/// <summary>The parsed marker structure of a JPEG 2000 codestream.</summary>
internal sealed class J2kCodestream
{
    public required J2kSiz Siz { get; init; }

    public required J2kHeaderSet Main { get; init; }

    public required List<J2kTilePart> TileParts { get; init; }

    public bool HasPpm { get; init; }

    /// <summary>Parses the codestream. A truncated codestream yields the tile-parts read so far.</summary>
    public static J2kCodestream Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        int pos = 0;
        if (span.Length < 4 || span[0] != 0xFF || span[1] != 0x4F)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 codestream does not start with an SOC marker.");
        }

        pos = 2;
        J2kSiz? siz = null;
        J2kHeaderSet? main = null;
        var ppm = new List<(int Index, byte[] Data)>();

        // Main header.
        while (true)
        {
            if (pos + 2 > span.Length)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 main header is truncated.");
            }

            int marker = (span[pos] << 8) | span[pos + 1];
            if (marker == 0xFF90 || marker == 0xFFD9)
            {
                break;
            }

            if ((marker & 0xFF00) != 0xFF00)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 main header contains an invalid marker.");
            }

            pos += 2;
            if (pos + 2 > span.Length)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 main header is truncated.");
            }

            int length = (span[pos] << 8) | span[pos + 1];
            if (length < 2 || pos + length > span.Length)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 marker segment is truncated.");
            }

            var body = span.Slice(pos + 2, length - 2);
            if (marker == 0xFF51)
            {
                siz = ParseSiz(body);
                main = new J2kHeaderSet(siz.Components.Length);
            }
            else if (siz is null || main is null)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 main header must begin with a SIZ marker segment.");
            }
            else if (marker == 0xFF60)
            {
                if (body.Length < 1)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 PPM marker segment is empty.");
                }

                ppm.Add((body[0], body[1..].ToArray()));
            }
            else
            {
                ParseHeaderMarker(marker, body, siz, main);
            }

            pos += length;
        }

        if (siz is null || main is null)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 codestream has no SIZ marker segment.");
        }

        if (main.Cod is null || main.Qcd is null)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 codestream lacks a COD or QCD marker segment.");
        }

        var parts = new List<J2kTilePart>();
        byte[] ppmData = [];
        if (ppm.Count > 0)
        {
            ppm.Sort((a, b) => a.Index.CompareTo(b.Index));
            ppmData = [.. ppm.SelectMany(p => p.Data)];
        }

        int ppmPos = 0;
        int tileCount = siz.TilesWide * siz.TilesHigh;
        while (pos + 2 <= span.Length)
        {
            int marker = (span[pos] << 8) | span[pos + 1];
            if (marker == 0xFFD9)
            {
                break;
            }

            if (marker != 0xFF90)
            {
                break; // Garbage or truncation after the last complete tile-part.
            }

            int sotStart = pos;
            if (pos + 12 > span.Length)
            {
                break;
            }

            int tileIndex = (span[pos + 4] << 8) | span[pos + 5];
            long psot = ((long)span[pos + 6] << 24) | ((long)span[pos + 7] << 16) | ((long)span[pos + 8] << 8) | span[pos + 9];
            int partIndex = span[pos + 10];
            if (tileIndex >= tileCount)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 tile-part refers to a tile that does not exist.");
            }

            pos += 12;
            var header = new J2kHeaderSet(siz.Components.Length);
            var part = new J2kTilePart { TileIndex = tileIndex, PartIndex = partIndex, Header = header };
            bool sawSod = false;
            while (pos + 2 <= span.Length)
            {
                int m = (span[pos] << 8) | span[pos + 1];
                pos += 2;
                if (m == 0xFF93)
                {
                    sawSod = true;
                    break;
                }

                if (pos + 2 > span.Length)
                {
                    break;
                }

                int length = (span[pos] << 8) | span[pos + 1];
                if (length < 2 || pos + length > span.Length)
                {
                    break;
                }

                var body = span.Slice(pos + 2, length - 2);
                if (m == 0xFF61)
                {
                    if (body.Length >= 1)
                    {
                        part.PptSegments.Add((body[0], body[1..].ToArray()));
                    }
                }
                else
                {
                    ParseHeaderMarker(m, body, siz, header);
                }

                pos += length;
            }

            if (!sawSod)
            {
                break;
            }

            long end;
            if (psot == 0)
            {
                end = span.Length;
                if (end >= 2 && span[(int)end - 2] == 0xFF && span[(int)end - 1] == 0xD9)
                {
                    end -= 2;
                }
            }
            else
            {
                end = Math.Min(sotStart + psot, span.Length);
            }

            if (end < pos)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 tile-part length is invalid.");
            }

            var partWithData = new J2kTilePart { TileIndex = tileIndex, PartIndex = partIndex, Header = header, Data = data.Slice(pos, (int)(end - pos)) };
            partWithData.PptSegments.AddRange(part.PptSegments);
            if (ppmData.Length > 0)
            {
                if (ppmPos + 4 > ppmData.Length)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 PPM data is truncated.");
                }

                long nppm = ((long)ppmData[ppmPos] << 24) | ((long)ppmData[ppmPos + 1] << 16) | ((long)ppmData[ppmPos + 2] << 8) | ppmData[ppmPos + 3];
                ppmPos += 4;
                if (nppm > ppmData.Length - ppmPos)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 PPM data is truncated.");
                }

                partWithData.PpmHeaders = ppmData.AsMemory(ppmPos, (int)nppm);
                ppmPos += (int)nppm;
            }

            parts.Add(partWithData);
            pos = (int)end;
        }

        return new J2kCodestream { Siz = siz, Main = main, TileParts = parts, HasPpm = ppmData.Length > 0 };
    }

    private static J2kSiz ParseSiz(ReadOnlySpan<byte> body)
    {
        if (body.Length < 36)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 SIZ marker segment is truncated.");
        }

        int capability = (body[0] << 8) | body[1];
        if ((capability & 0x8000) != 0 && (capability & 0x7FFF) > 2)
        {
            // Rsiz profile bits above Part 1 (Part 2 extensions) are tolerated; unsupported features are rejected where they occur.
        }

        long xsiz = ReadU32(body, 2);
        long ysiz = ReadU32(body, 6);
        long xosiz = ReadU32(body, 10);
        long yosiz = ReadU32(body, 14);
        long xtsiz = ReadU32(body, 18);
        long ytsiz = ReadU32(body, 22);
        long xtosiz = ReadU32(body, 26);
        long ytosiz = ReadU32(body, 30);
        int count = (body[34] << 8) | body[35];
        if (count == 0 || count > Jpeg2000DecodingLimits.MaxComponents || body.Length < 36 + (count * 3))
        {
            throw new Jpeg2000DecodingException("JPEG 2000 SIZ marker segment has an invalid component count.");
        }

        if (xsiz > int.MaxValue || ysiz > int.MaxValue || xtsiz == 0 || ytsiz == 0 || xtsiz > int.MaxValue || ytsiz > int.MaxValue
            || xosiz >= xsiz || yosiz >= ysiz || xtosiz > xosiz || ytosiz > yosiz || xtosiz + xtsiz <= xosiz || ytosiz + ytsiz <= yosiz)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 SIZ marker segment has an invalid image or tile geometry.");
        }

        if ((xsiz - xosiz) * (ysiz - yosiz) > Jpeg2000DecodingLimits.MaxPixelCount)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 image dimensions exceed the maximum supported pixel count.");
        }

        var components = new J2kComponentInfo[count];
        for (int i = 0; i < count; i++)
        {
            int ssiz = body[36 + (i * 3)];
            int xr = body[37 + (i * 3)];
            int yr = body[38 + (i * 3)];
            if (xr == 0 || yr == 0)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 component has a zero sub-sampling factor.");
            }

            int depth = (ssiz & 0x7F) + 1;
            if (depth > 16)
            {
                throw new Jpeg2000UnsupportedFeatureException($"JPEG 2000 component bit depth {depth} is not supported (16 bits is the maximum).");
            }

            components[i] = new J2kComponentInfo(depth, (ssiz & 0x80) != 0, xr, yr);
        }

        var siz = new J2kSiz
        {
            Xsiz = (int)xsiz,
            Ysiz = (int)ysiz,
            XOsiz = (int)xosiz,
            YOsiz = (int)yosiz,
            XTsiz = (int)xtsiz,
            YTsiz = (int)ytsiz,
            XTOsiz = (int)xtosiz,
            YTOsiz = (int)ytosiz,
            Components = components,
        };

        long tiles = (long)siz.TilesWide * siz.TilesHigh;
        if (tiles > Jpeg2000DecodingLimits.MaxTiles)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 image has too many tiles.");
        }

        return siz;
    }

    private static void ParseHeaderMarker(int marker, ReadOnlySpan<byte> body, J2kSiz siz, J2kHeaderSet set)
    {
        int componentCount = siz.Components.Length;
        int componentBytes = componentCount < 257 ? 1 : 2;
        switch (marker)
        {
            case 0xFF52: // COD
                set.Cod = ParseCod(body);
                break;
            case 0xFF53: // COC
            {
                int component = ReadComponent(body, componentBytes);
                if (component >= componentCount)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 COC marker refers to a component that does not exist.");
                }

                int offset = componentBytes;
                if (body.Length < offset + 1)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 COC marker segment is truncated.");
                }

                bool precincts = (body[offset] & 1) != 0;
                set.Coc[component] = ParseComponentCoding(body[(offset + 1)..], precincts);
                break;
            }

            case 0xFF5C: // QCD
                set.Qcd = ParseQuantization(body);
                break;
            case 0xFF5D: // QCC
            {
                int component = ReadComponent(body, componentBytes);
                if (component >= componentCount)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 QCC marker refers to a component that does not exist.");
                }

                set.Qcc[component] = ParseQuantization(body[componentBytes..]);
                break;
            }

            case 0xFF5E: // RGN
            {
                int component = ReadComponent(body, componentBytes);
                if (component >= componentCount || body.Length < componentBytes + 2)
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 RGN marker segment is invalid.");
                }

                if (body[componentBytes] != 0)
                {
                    throw new Jpeg2000UnsupportedFeatureException("JPEG 2000 region-of-interest style other than maxshift is not supported.");
                }

                set.Rgn[component] = body[componentBytes + 1];
                break;
            }

            case 0xFF5F: // POC
            {
                int entrySize = 5 + (2 * componentBytes);
                var list = set.Poc ??= [];
                for (int p = 0; p + entrySize <= body.Length; p += entrySize)
                {
                    int rs = body[p];
                    int cs = componentBytes == 1 ? body[p + 1] : (body[p + 1] << 8) | body[p + 2];
                    int lye = (body[p + 1 + componentBytes] << 8) | body[p + 2 + componentBytes];
                    int re = body[p + 3 + componentBytes];
                    int ce = componentBytes == 1 ? body[p + 4 + componentBytes] : (body[p + 4 + componentBytes] << 8) | body[p + 5 + componentBytes];
                    if (componentBytes == 1 && ce == 0)
                    {
                        ce = 256;
                    }

                    int order = body[p + 4 + (2 * componentBytes)];
                    list.Add(new J2kProgressionChange(rs, cs, lye, re, ce, order));
                }

                break;
            }

            default:
                // TLM, PLM, PLT, CRG, COM and unknown markers carry nothing the decoder needs.
                break;
        }
    }

    private static int ReadComponent(ReadOnlySpan<byte> body, int bytes)
    {
        if (body.Length < bytes)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 marker segment is truncated.");
        }

        return bytes == 1 ? body[0] : (body[0] << 8) | body[1];
    }

    private static J2kCod ParseCod(ReadOnlySpan<byte> body)
    {
        if (body.Length < 10)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 COD marker segment is truncated.");
        }

        int scod = body[0];
        int layers = (body[2] << 8) | body[3];
        if (layers == 0)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 COD declares zero quality layers.");
        }

        if (body[1] > 4)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 COD declares an invalid progression order.");
        }

        return new J2kCod
        {
            Sop = (scod & 2) != 0,
            Eph = (scod & 4) != 0,
            Progression = body[1],
            Layers = layers,
            Mct = body[4] != 0,
            Coding = ParseComponentCoding(body[5..], (scod & 1) != 0),
        };
    }

    private static J2kComponentCoding ParseComponentCoding(ReadOnlySpan<byte> body, bool precincts)
    {
        if (body.Length < 5)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 coding style marker segment is truncated.");
        }

        int levels = body[0];
        if (levels > 32)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 declares too many decomposition levels.");
        }

        int cbw = (body[1] & 0xF) + 2;
        int cbh = (body[2] & 0xF) + 2;
        if (cbw > 10 || cbh > 10 || cbw + cbh > 12)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 declares an invalid code-block size.");
        }

        int style = body[3];
        if ((style & 0xC0) != 0)
        {
            throw new Jpeg2000UnsupportedFeatureException("JPEG 2000 Part 2 code-block style extensions are not supported.");
        }

        int transform = body[4];
        if (transform > 1)
        {
            throw new Jpeg2000UnsupportedFeatureException("JPEG 2000 Part 2 wavelet transforms are not supported.");
        }

        byte[]? sizes = null;
        if (precincts)
        {
            if (body.Length < 5 + levels + 1)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 coding style marker segment is truncated.");
            }

            sizes = body.Slice(5, levels + 1).ToArray();
            for (int r = 0; r <= levels; r++)
            {
                if (r > 0 && ((sizes[r] & 0xF) == 0 || (sizes[r] >> 4) == 0))
                {
                    throw new Jpeg2000DecodingException("JPEG 2000 declares a precinct size of one at a resolution that has subbands.");
                }
            }
        }

        return new J2kComponentCoding
        {
            Levels = levels,
            CodeBlockWidthExponent = cbw,
            CodeBlockHeightExponent = cbh,
            CodeBlockStyle = style,
            Reversible = transform == 1,
            PrecinctSizes = sizes,
        };
    }

    private static J2kQuantization ParseQuantization(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
        {
            throw new Jpeg2000DecodingException("JPEG 2000 quantization marker segment is empty.");
        }

        int style = body[0] & 0x1F;
        int guard = body[0] >> 5;
        var payload = body[1..];
        int[] exponents;
        int[] mantissas;
        if (style == 0)
        {
            exponents = new int[payload.Length];
            mantissas = new int[payload.Length];
            for (int i = 0; i < payload.Length; i++)
            {
                exponents[i] = payload[i] >> 3;
            }
        }
        else if (style is 1 or 2)
        {
            int count = payload.Length / 2;
            if (style == 1 && count < 1)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 quantization marker segment is truncated.");
            }

            exponents = new int[count];
            mantissas = new int[count];
            for (int i = 0; i < count; i++)
            {
                int value = (payload[2 * i] << 8) | payload[(2 * i) + 1];
                exponents[i] = value >> 11;
                mantissas[i] = value & 0x7FF;
            }
        }
        else
        {
            throw new Jpeg2000DecodingException("JPEG 2000 declares an invalid quantization style.");
        }

        return new J2kQuantization { Style = style, GuardBits = guard, Exponents = exponents, Mantissas = mantissas };
    }

    private static long ReadU32(ReadOnlySpan<byte> data, int offset) =>
        ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];
}
