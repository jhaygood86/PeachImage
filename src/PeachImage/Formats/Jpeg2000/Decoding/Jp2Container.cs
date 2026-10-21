namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>A palette from a JP2 <c>pclr</c> box.</summary>
internal sealed class Jp2Palette
{
    public required int Entries { get; init; }

    public required int[] Depths { get; init; }

    public required bool[] Signed { get; init; }

    /// <summary>Entry-major values: <c>Values[entry * columns + column]</c>.</summary>
    public required int[] Values { get; init; }

    public int Columns => Depths.Length;
}

/// <summary>One <c>cmap</c> entry: which component feeds a channel, directly or through a palette column.</summary>
internal readonly record struct Jp2ComponentMapping(int Component, bool UsesPalette, int PaletteColumn);

/// <summary>One <c>cdef</c> entry.</summary>
internal readonly record struct Jp2ChannelDefinition(int Channel, int Type, int Association);

/// <summary>What a JP2 file says about its codestream beyond the codestream itself (T.800 Annex I).</summary>
internal sealed class Jp2Info
{
    public ReadOnlyMemory<byte> Codestream { get; set; }

    /// <summary>The enumerated colour space of the first <c>colr</c> box using method 1, or 0.</summary>
    public int EnumeratedColorSpace { get; set; }

    /// <summary>The ICC profile of the first <c>colr</c> box using method 2 (a "restricted" ICC profile), if any.</summary>
    public byte[]? IccProfile { get; set; }

    public Jp2Palette? Palette { get; set; }

    public Jp2ComponentMapping[]? ComponentMappings { get; set; }

    public Jp2ChannelDefinition[]? ChannelDefinitions { get; set; }

    public static bool HasJp2Signature(ReadOnlySpan<byte> header) =>
        header.Length >= 12
        && header[0] == 0 && header[1] == 0 && header[2] == 0 && header[3] == 0x0C
        && header[4] == 0x6A && header[5] == 0x50 && header[6] == 0x20 && header[7] == 0x20
        && header[8] == 0x0D && header[9] == 0x0A && header[10] == 0x87 && header[11] == 0x0A;

    public static bool HasCodestreamSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == 0xFF && header[1] == 0x4F && header[2] == 0xFF && header[3] == 0x51;

    /// <summary>Walks the boxes of a JP2 file, collecting the colour description and locating the first contiguous codestream box.</summary>
    public static Jp2Info Parse(ReadOnlyMemory<byte> file)
    {
        var info = new Jp2Info();
        ParseBoxes(file, info, 0);
        if (info.Codestream.IsEmpty)
        {
            throw new Jpeg2000DecodingException("JP2 file contains no codestream box.");
        }

        return info;
    }

    private static void ParseBoxes(ReadOnlyMemory<byte> data, Jp2Info info, int depth)
    {
        var span = data.Span;
        int pos = 0;
        while (pos + 8 <= span.Length)
        {
            long length = ReadU32(span, pos);
            int type = (int)ReadU32(span, pos + 4);
            int header = 8;
            if (length == 1)
            {
                if (pos + 16 > span.Length)
                {
                    break;
                }

                length = ((long)ReadU32(span, pos + 8) << 32) | ReadU32(span, pos + 12);
                header = 16;
            }
            else if (length == 0)
            {
                length = span.Length - pos;
            }

            if (length < header || pos + length > span.Length)
            {
                // A truncated final box: use what is there for the codestream, otherwise stop.
                if (type == 0x6A703263 && length >= header)
                {
                    length = span.Length - pos;
                }
                else
                {
                    break;
                }
            }

            var body = data.Slice(pos + header, (int)(length - header));
            switch (type)
            {
                case 0x6A703268 when depth == 0: // jp2h
                    ParseBoxes(body, info, depth + 1);
                    break;
                case 0x636F6C72: // colr
                    ParseColr(body.Span, info);
                    break;
                case 0x70636C72: // pclr
                    info.Palette ??= ParsePalette(body.Span);
                    break;
                case 0x636D6170: // cmap
                    info.ComponentMappings ??= ParseCmap(body.Span);
                    break;
                case 0x63646566: // cdef
                    info.ChannelDefinitions ??= ParseCdef(body.Span);
                    break;
                case 0x6A703263 when depth == 0: // jp2c
                    if (info.Codestream.IsEmpty)
                    {
                        info.Codestream = body;
                    }

                    break;
            }

            pos += (int)length;
        }
    }

    private static void ParseColr(ReadOnlySpan<byte> body, Jp2Info info)
    {
        if (body.Length < 3)
        {
            return;
        }

        int method = body[0];
        if (method == 1 && body.Length >= 7 && info.EnumeratedColorSpace == 0 && info.IccProfile is null)
        {
            info.EnumeratedColorSpace = (int)ReadU32(body, 3);
        }
        else if (method == 2 && info.EnumeratedColorSpace == 0 && info.IccProfile is null)
        {
            info.IccProfile = body[3..].ToArray();
        }
    }

    private static Jp2Palette? ParsePalette(ReadOnlySpan<byte> body)
    {
        if (body.Length < 3)
        {
            return null;
        }

        int entries = (body[0] << 8) | body[1];
        int columns = body[2];
        if (entries == 0 || entries > 1024 || columns == 0 || body.Length < 3 + columns)
        {
            return null;
        }

        var depths = new int[columns];
        var signed = new bool[columns];
        int pos = 3;
        for (int i = 0; i < columns; i++)
        {
            depths[i] = (body[pos] & 0x7F) + 1;
            signed[i] = (body[pos] & 0x80) != 0;
            pos++;
            if (depths[i] > 16)
            {
                return null;
            }
        }

        var values = new int[entries * columns];
        for (int e = 0; e < entries; e++)
        {
            for (int c = 0; c < columns; c++)
            {
                int bytes = (depths[c] + 7) / 8;
                if (pos + bytes > body.Length)
                {
                    return null;
                }

                int value = 0;
                for (int b = 0; b < bytes; b++)
                {
                    value = (value << 8) | body[pos++];
                }

                values[(e * columns) + c] = value;
            }
        }

        return new Jp2Palette { Entries = entries, Depths = depths, Signed = signed, Values = values };
    }

    private static Jp2ComponentMapping[] ParseCmap(ReadOnlySpan<byte> body)
    {
        int count = body.Length / 4;
        var result = new Jp2ComponentMapping[count];
        for (int i = 0; i < count; i++)
        {
            int component = (body[4 * i] << 8) | body[(4 * i) + 1];
            result[i] = new Jp2ComponentMapping(component, body[(4 * i) + 2] == 1, body[(4 * i) + 3]);
        }

        return result;
    }

    private static Jp2ChannelDefinition[]? ParseCdef(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2)
        {
            return null;
        }

        int count = Math.Min((body[0] << 8) | body[1], (body.Length - 2) / 6);
        var result = new Jp2ChannelDefinition[count];
        for (int i = 0; i < count; i++)
        {
            int offset = 2 + (6 * i);
            result[i] = new Jp2ChannelDefinition(
                (body[offset] << 8) | body[offset + 1],
                (body[offset + 2] << 8) | body[offset + 3],
                (body[offset + 4] << 8) | body[offset + 5]);
        }

        return result;
    }

    private static long ReadU32(ReadOnlySpan<byte> data, int offset) =>
        ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];
}
