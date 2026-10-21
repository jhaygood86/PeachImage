using System.Buffers;

namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>Decides how a codestream's components map to an output image and builds that image from the decoded component planes.</summary>
internal sealed class J2kImagePlan
{
    private readonly record struct Channel(int Component, int PaletteColumn, int Depth);

    private Channel[] _color = [];
    private Channel _alpha;
    private bool _hasAlpha;

    public PixelFormat Format { get; private set; }

    public bool HasAlpha => _hasAlpha;

    public bool Sycc { get; private set; }

    public bool Cmyk { get; private set; }

    public Jp2Palette? Palette { get; private set; }

    public static J2kImagePlan Create(J2kSiz siz, Jp2Info? jp2, bool ignoreColorSpace)
    {
        var plan = new J2kImagePlan();
        int componentCount = siz.Components.Length;
        var channels = new List<Channel>();
        Jp2Palette? palette = null;
        if (!ignoreColorSpace && jp2?.Palette is { } p && jp2.ComponentMappings is { Length: > 0 } maps)
        {
            palette = p;
            foreach (var map in maps)
            {
                if (map.Component >= componentCount)
                {
                    continue;
                }

                if (map.UsesPalette && map.PaletteColumn < p.Columns)
                {
                    channels.Add(new Channel(map.Component, map.PaletteColumn, p.Depths[map.PaletteColumn]));
                }
                else if (!map.UsesPalette)
                {
                    channels.Add(new Channel(map.Component, -1, siz.Components[map.Component].Depth));
                }
            }
        }

        if (channels.Count == 0)
        {
            palette = null;
            for (int c = 0; c < componentCount; c++)
            {
                channels.Add(new Channel(c, -1, siz.Components[c].Depth));
            }
        }

        plan.Palette = palette;

        // Which channels are colour, and which one (if any) is opacity.
        var colors = new List<Channel>();
        Channel? alpha = null;
        int enumerated = ignoreColorSpace ? 0 : (jp2?.EnumeratedColorSpace ?? 0);
        bool hasIcc = !ignoreColorSpace && jp2?.IccProfile is not null;

        if (!ignoreColorSpace && jp2?.ChannelDefinitions is { Length: > 0 } definitions)
        {
            var byAssociation = new SortedDictionary<int, Channel>();
            foreach (var d in definitions)
            {
                if (d.Channel >= channels.Count)
                {
                    continue;
                }

                if (d.Type == 0 && d.Association >= 1 && d.Association < 65535)
                {
                    byAssociation[d.Association] = channels[d.Channel];
                }
                else if (d.Type is 1 or 2 && alpha is null)
                {
                    alpha = channels[d.Channel];
                }
            }

            colors.AddRange(byAssociation.Values);
        }

        if (colors.Count == 0)
        {
            int expectedColors = enumerated switch
            {
                17 => 1,
                12 => 4,
                16 or 18 or 20 => 3,
                _ => 0,
            };
            if (expectedColors == 0)
            {
                // No colour specification: decide from the channel count.
                int n = channels.Count;
                expectedColors = hasIcc && n >= 3 ? 3 : n switch
                {
                    1 => 1,
                    2 => 1,
                    3 => 3,
                    4 => ignoreColorSpace ? 4 : 3,
                    _ => 3,
                };
                if (hasIcc && n == 1)
                {
                    expectedColors = 1;
                }

                if (!ignoreColorSpace && jp2 is null && n == 4)
                {
                    expectedColors = 3; // A bare codestream with four components: RGBA.
                }
            }

            expectedColors = Math.Min(expectedColors, channels.Count);
            colors.AddRange(channels.Take(expectedColors));
            if (alpha is null && channels.Count > expectedColors && (enumerated == 0 || jp2 is null) && expectedColors <= 3 && !ignoreColorSpace)
            {
                alpha = channels[expectedColors];
            }
            else if (alpha is null && ignoreColorSpace && channels.Count == 2)
            {
                alpha = channels[1];
            }
        }

        if (colors.Count is not (1 or 3 or 4))
        {
            colors = colors.Count >= 3 ? colors.Take(3).ToList() : colors.Take(1).ToList();
        }

        plan._color = [.. colors];
        plan.Sycc = enumerated == 18 && colors.Count == 3;
        plan.Cmyk = colors.Count == 4;
        if (alpha is { } a && !plan.Cmyk)
        {
            plan._alpha = a;
            plan._hasAlpha = true;
        }

        bool wide = plan._color.Any(ch => ch.Depth > 8) || (plan._hasAlpha && plan._alpha.Depth > 8);
        if (plan.Cmyk)
        {
            plan.Format = PixelFormat.Cmyk32;
        }
        else if (plan._color.Length == 1 && !plan._hasAlpha)
        {
            plan.Format = wide ? PixelFormat.Gray16 : PixelFormat.Gray8;
        }
        else if (plan._hasAlpha)
        {
            plan.Format = wide ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        }
        else
        {
            plan.Format = wide ? PixelFormat.Rgb48 : PixelFormat.Rgb24;
        }

        return plan;
    }

    /// <summary>Builds the image: a row-wise pass that upsamples, expands palettes, rescales depths and interleaves.</summary>
    public Image Build(J2kSiz siz, int reduce, ushort[][] planes, int[] planeWidths, int[] planeHeights, int[] planeOriginX, int[] planeOriginY, int width, int height, int gridOriginX, int gridOriginY)
    {
        var image = Image.Create(width, height, Format);
        int channelCount = _color.Length + (_hasAlpha ? 1 : 0);
        var all = new Channel[channelCount];
        _color.CopyTo(all, 0);
        if (_hasAlpha)
        {
            all[^1] = _alpha;
        }

        bool wide = Format is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
        int outDepth = wide ? 16 : 8;

        // Per-channel conversion tables from native sample value to the output depth.
        var tables = new ushort[channelCount][];
        for (int k = 0; k < channelCount; k++)
        {
            tables[k] = BuildScaleTable(all[k].Depth, outDepth, Palette, all[k]);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        int rowsPerChunk = 16;
        int chunks = (height + rowsPerChunk - 1) / rowsPerChunk;
        void Body(int chunk)
        {
            ushort[][] rows = new ushort[channelCount][];
            for (int k = 0; k < channelCount; k++)
            {
                rows[k] = ArrayPool<ushort>.Shared.Rent(width);
            }

            try
            {
                int yEnd = Math.Min(height, (chunk + 1) * rowsPerChunk);
                for (int y = chunk * rowsPerChunk; y < yEnd; y++)
                {
                    for (int k = 0; k < channelCount; k++)
                    {
                        FetchRow(siz, all[k], planes, planeWidths, planeHeights, planeOriginX, planeOriginY, gridOriginX, gridOriginY, y, width, rows[k]);
                        ApplyTable(rows[k].AsSpan(0, width), tables[k], Palette is not null && all[k].PaletteColumn >= 0);
                    }

                    WriteRow(image.GetRowSpan(y), rows, width);
                }
            }
            finally
            {
                for (int k = 0; k < channelCount; k++)
                {
                    ArrayPool<ushort>.Shared.Return(rows[k]);
                }
            }
        }

        if (chunks > 1 && (long)width * height >= 65536)
        {
            Parallel.For(0, chunks, options, Body);
        }
        else
        {
            for (int c = 0; c < chunks; c++)
            {
                Body(c);
            }
        }

        return image;
    }

    private static void FetchRow(
        J2kSiz siz,
        Channel channel,
        ushort[][] planes,
        int[] planeWidths,
        int[] planeHeights,
        int[] planeOriginX,
        int[] planeOriginY,
        int gridOriginX,
        int gridOriginY,
        int y,
        int width,
        ushort[] destination)
    {
        int component = channel.Component;
        var info = siz.Components[component];
        int planeWidth = planeWidths[component];
        int gy = gridOriginY + y;
        int row = (gy / info.YRsiz) - planeOriginY[component];
        row = Math.Clamp(row, 0, planeHeights[component] - 1);
        var source = planes[component].AsSpan(row * planeWidth, planeWidth);
        var target = destination.AsSpan(0, width);
        if (info.XRsiz == 1 && planeWidth >= width && planeOriginX[component] == gridOriginX)
        {
            source[..width].CopyTo(target);
            return;
        }

        for (int x = 0; x < width; x++)
        {
            int column = ((gridOriginX + x) / info.XRsiz) - planeOriginX[component];
            target[x] = source[Math.Clamp(column, 0, planeWidth - 1)];
        }
    }

    private static void ApplyTable(Span<ushort> row, ushort[] table, bool isPalette)
    {
        // Palette channels hold indices at this point and the table maps index -> scaled value; others map native value -> scaled value.
        _ = isPalette;
        for (int i = 0; i < row.Length; i++)
        {
            int v = row[i];
            row[i] = table[v < table.Length ? v : table.Length - 1];
        }
    }

    private static ushort[] BuildScaleTable(int depth, int outDepth, Jp2Palette? palette, Channel channel)
    {
        int outMax = (1 << outDepth) - 1;
        if (palette is not null && channel.PaletteColumn >= 0)
        {
            var table = new ushort[palette.Entries];
            int max = (1 << palette.Depths[channel.PaletteColumn]) - 1;
            for (int e = 0; e < palette.Entries; e++)
            {
                int value = palette.Values[(e * palette.Columns) + channel.PaletteColumn];
                if (palette.Signed[channel.PaletteColumn])
                {
                    value += 1 << (palette.Depths[channel.PaletteColumn] - 1);
                }

                value = Math.Clamp(value, 0, max);
                table[e] = Scale(value, max, outMax);
            }

            return table;
        }

        int nativeMax = (1 << depth) - 1;
        var identity = new ushort[nativeMax + 1];
        for (int v = 0; v <= nativeMax; v++)
        {
            identity[v] = Scale(v, nativeMax, outMax);
        }

        return identity;
    }

    private static ushort Scale(int value, int max, int outMax) =>
        max == outMax ? (ushort)value : (ushort)(((value * (long)outMax) + (max / 2)) / max);

    private void WriteRow(Span<byte> destination, ushort[][] rows, int width)
    {
        switch (Format)
        {
            case PixelFormat.Gray8:
                for (int x = 0; x < width; x++)
                {
                    destination[x] = (byte)rows[0][x];
                }

                break;

            case PixelFormat.Gray16:
                WriteWide(destination, rows, width, 1);
                break;

            case PixelFormat.Rgb24:
            case PixelFormat.Rgba32:
            {
                int stride = Format == PixelFormat.Rgb24 ? 3 : 4;
                if (_color.Length == 1)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte g = (byte)rows[0][x];
                        destination[(x * stride)] = g;
                        destination[(x * stride) + 1] = g;
                        destination[(x * stride) + 2] = g;
                        if (stride == 4)
                        {
                            destination[(x * stride) + 3] = (byte)rows[1][x];
                        }
                    }
                }
                else if (Sycc)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float yy = rows[0][x];
                        float cb = rows[1][x] - 128f;
                        float cr = rows[2][x] - 128f;
                        destination[(x * stride)] = ClampByte(yy + (1.402f * cr));
                        destination[(x * stride) + 1] = ClampByte(yy - (0.344136f * cb) - (0.714136f * cr));
                        destination[(x * stride) + 2] = ClampByte(yy + (1.772f * cb));
                        if (stride == 4)
                        {
                            destination[(x * stride) + 3] = (byte)rows[3][x];
                        }
                    }
                }
                else
                {
                    var r = rows[0];
                    var g = rows[1];
                    var b = rows[2];
                    for (int x = 0; x < width; x++)
                    {
                        destination[(x * stride)] = (byte)r[x];
                        destination[(x * stride) + 1] = (byte)g[x];
                        destination[(x * stride) + 2] = (byte)b[x];
                    }

                    if (stride == 4)
                    {
                        var a = rows[3];
                        for (int x = 0; x < width; x++)
                        {
                            destination[(x * stride) + 3] = (byte)a[x];
                        }
                    }
                }

                break;
            }

            case PixelFormat.Cmyk32:
                for (int x = 0; x < width; x++)
                {
                    destination[(x * 4)] = (byte)rows[0][x];
                    destination[(x * 4) + 1] = (byte)rows[1][x];
                    destination[(x * 4) + 2] = (byte)rows[2][x];
                    destination[(x * 4) + 3] = (byte)rows[3][x];
                }

                break;

            default:
                WriteWide(destination, rows, width, Format == PixelFormat.Rgb48 ? 3 : 4);
                break;
        }
    }

    private void WriteWide(Span<byte> destination, ushort[][] rows, int width, int samples)
    {
        var target = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(destination);
        if (_color.Length == 1 && samples >= 3)
        {
            for (int x = 0; x < width; x++)
            {
                ushort g = rows[0][x];
                target[x * samples] = g;
                target[(x * samples) + 1] = g;
                target[(x * samples) + 2] = g;
                if (samples == 4)
                {
                    target[(x * samples) + 3] = rows[1][x];
                }
            }

            return;
        }

        if (samples == 1)
        {
            rows[0].AsSpan(0, width).CopyTo(target);
            return;
        }

        bool ycc = Sycc;
        for (int x = 0; x < width; x++)
        {
            if (ycc)
            {
                float yy = rows[0][x];
                float cb = rows[1][x] - 32768f;
                float cr = rows[2][x] - 32768f;
                target[x * samples] = ClampWide(yy + (1.402f * cr));
                target[(x * samples) + 1] = ClampWide(yy - (0.344136f * cb) - (0.714136f * cr));
                target[(x * samples) + 2] = ClampWide(yy + (1.772f * cb));
            }
            else
            {
                target[x * samples] = rows[0][x];
                target[(x * samples) + 1] = rows[1][x];
                target[(x * samples) + 2] = rows[2][x];
            }

            if (samples == 4)
            {
                target[(x * samples) + 3] = rows[3][x];
            }
        }
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    private static ushort ClampWide(float v) => (ushort)Math.Clamp((int)MathF.Round(v), 0, 65535);
}
