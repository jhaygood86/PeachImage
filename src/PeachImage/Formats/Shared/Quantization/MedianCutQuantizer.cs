namespace PeachImage.Formats.Shared.Quantization;

/// <summary>
/// Builds a palette of at most <c>maxColors</c> entries from a <see cref="ColorHistogram"/> using
/// frequency-weighted median-cut box splitting (Heckbert's algorithm), the same general approach used by
/// gifsicle/ffmpeg's GIF encoder. If the source has at most <c>maxColors</c> distinct colors, every color is
/// kept exactly — this is what makes encode→decode round trips of low-color-count images lossless.
/// </summary>
internal static class MedianCutQuantizer
{
    /// <summary>Builds a flat RGB-triple palette (length = entry count * 3), with at most <paramref name="maxColors"/> entries.</summary>
    public static byte[] BuildPalette(ColorHistogram histogram, int maxColors)
    {
        maxColors = Math.Clamp(maxColors, 1, 256);

        var entries = new List<ColorEntry>(histogram.Counts.Count);
        foreach (var (key, count) in histogram.Counts)
        {
            entries.Add(new ColorEntry((byte)(key >> 16), (byte)(key >> 8), (byte)key, count));
        }

        if (entries.Count == 0)
        {
            return [0, 0, 0];
        }

        if (entries.Count <= maxColors)
        {
            return Flatten(entries.Select(static e => (e.R, e.G, e.B)));
        }

        var boxes = new List<ColorBox> { new(entries) };
        while (boxes.Count < maxColors)
        {
            int splitIndex = FindLargestSplittableBox(boxes);
            if (splitIndex < 0)
            {
                break;
            }

            var (a, b) = boxes[splitIndex].Split();
            boxes.RemoveAt(splitIndex);
            boxes.Add(a);
            boxes.Add(b);
        }

        return Flatten(boxes.Select(static b => b.WeightedAverage()));
    }

    private static int FindLargestSplittableBox(List<ColorBox> boxes)
    {
        int bestIndex = -1;
        long bestPopulation = -1;

        for (int i = 0; i < boxes.Count; i++)
        {
            if (!boxes[i].CanSplit)
            {
                continue;
            }

            long population = boxes[i].Population;
            if (population > bestPopulation)
            {
                bestPopulation = population;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static byte[] Flatten(IEnumerable<(byte R, byte G, byte B)> colors)
    {
        var list = colors.ToList();
        byte[] palette = new byte[list.Count * 3];
        for (int i = 0; i < list.Count; i++)
        {
            palette[(i * 3) + 0] = list[i].R;
            palette[(i * 3) + 1] = list[i].G;
            palette[(i * 3) + 2] = list[i].B;
        }

        return palette;
    }

    private readonly record struct ColorEntry(byte R, byte G, byte B, int Count);

    private sealed class ColorBox
    {
        private readonly List<ColorEntry> _entries;

        public ColorBox(List<ColorEntry> entries) => _entries = entries;

        public bool CanSplit => _entries.Count > 1;

        public long Population
        {
            get
            {
                long total = 0;
                foreach (var e in _entries)
                {
                    total += e.Count;
                }

                return total;
            }
        }

        public (byte R, byte G, byte B) WeightedAverage()
        {
            long r = 0, g = 0, b = 0, total = 0;
            foreach (var e in _entries)
            {
                r += (long)e.R * e.Count;
                g += (long)e.G * e.Count;
                b += (long)e.B * e.Count;
                total += e.Count;
            }

            if (total == 0)
            {
                total = 1;
            }

            return ((byte)(r / total), (byte)(g / total), (byte)(b / total));
        }

        public (ColorBox, ColorBox) Split()
        {
            byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            foreach (var e in _entries)
            {
                minR = Math.Min(minR, e.R); maxR = Math.Max(maxR, e.R);
                minG = Math.Min(minG, e.G); maxG = Math.Max(maxG, e.G);
                minB = Math.Min(minB, e.B); maxB = Math.Max(maxB, e.B);
            }

            int rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;

            Comparison<ColorEntry> comparer = (rangeR >= rangeG && rangeR >= rangeB)
                ? static (x, y) => x.R.CompareTo(y.R)
                : (rangeG >= rangeB)
                    ? static (x, y) => x.G.CompareTo(y.G)
                    : static (x, y) => x.B.CompareTo(y.B);

            _entries.Sort(comparer);

            long total = Population;
            long half = total / 2;
            long running = 0;
            int splitAt = 1;
            for (int i = 0; i < _entries.Count; i++)
            {
                running += _entries[i].Count;
                if (running >= half)
                {
                    splitAt = i + 1;
                    break;
                }
            }

            splitAt = Math.Clamp(splitAt, 1, _entries.Count - 1);

            var left = _entries.GetRange(0, splitAt);
            var right = _entries.GetRange(splitAt, _entries.Count - splitAt);
            return (new ColorBox(left), new ColorBox(right));
        }
    }
}
