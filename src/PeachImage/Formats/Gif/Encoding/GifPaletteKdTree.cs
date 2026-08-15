namespace PeachImage.Formats.Gif.Encoding;

/// <summary>
/// A 3-D (R/G/B) k-d tree over a GIF color table, for exact nearest-color queries in O(log n) average instead
/// of an O(n) linear scan over every palette entry. Built once per encode and reused across every pixel
/// (and, for animated GIFs, every frame) — the whole point is amortizing the O(n log n) build cost against
/// millions of per-pixel queries. Returns exactly the same nearest index a linear scan would (this is a
/// speed-only change, not an approximation), except that ties (multiple palette entries at the identical
/// minimum distance) may resolve to a different one of the tied entries than array-order linear scan would.
/// </summary>
internal sealed class GifPaletteKdTree
{
    private struct Node
    {
        public byte R, G, B;
        public int PaletteIndex;
        public int Left;
        public int Right;
    }

    private readonly Node[] _nodes;
    private readonly int _root;

    public GifPaletteKdTree(byte[] palette)
    {
        Palette = palette;
        int count = palette.Length / 3;
        var points = new (byte R, byte G, byte B, int Index)[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = (palette[i * 3], palette[(i * 3) + 1], palette[(i * 3) + 2], i);
        }

        _nodes = new Node[count];
        int nextSlot = 0;
        _root = count == 0 ? -1 : Build(points, 0, count, depth: 0, ref nextSlot);
    }

    /// <summary>The flat RGB-triple palette this tree was built from.</summary>
    public byte[] Palette { get; }

    public int NearestIndex(byte r, byte g, byte b)
    {
        int best = _nodes[_root].PaletteIndex;
        int bestDist = int.MaxValue;
        Search(_root, r, g, b, depth: 0, ref best, ref bestDist);
        return best;
    }

    private int Build(Span<(byte R, byte G, byte B, int Index)> points, int start, int end, int depth, ref int nextSlot)
    {
        if (start >= end)
        {
            return -1;
        }

        int axis = depth % 3;
        var slice = points[start..end];
        switch (axis)
        {
            case 0:
                slice.Sort(static (a, b) => a.R.CompareTo(b.R));
                break;
            case 1:
                slice.Sort(static (a, b) => a.G.CompareTo(b.G));
                break;
            default:
                slice.Sort(static (a, b) => a.B.CompareTo(b.B));
                break;
        }

        int mid = start + ((end - start) / 2);
        int nodeSlot = nextSlot++;
        var point = points[mid];

        int left = Build(points, start, mid, depth + 1, ref nextSlot);
        int right = Build(points, mid + 1, end, depth + 1, ref nextSlot);

        _nodes[nodeSlot] = new Node
        {
            R = point.R,
            G = point.G,
            B = point.B,
            PaletteIndex = point.Index,
            Left = left,
            Right = right,
        };

        return nodeSlot;
    }

    private void Search(int nodeIndex, byte r, byte g, byte b, int depth, ref int best, ref int bestDist)
    {
        if (nodeIndex < 0)
        {
            return;
        }

        ref readonly Node node = ref _nodes[nodeIndex];
        int dr = r - node.R, dg = g - node.G, db = b - node.B;
        int dist = (dr * dr) + (dg * dg) + (db * db);
        if (dist < bestDist)
        {
            bestDist = dist;
            best = node.PaletteIndex;
        }

        int axis = depth % 3;
        int diff = axis switch
        {
            0 => r - node.R,
            1 => g - node.G,
            _ => b - node.B,
        };

        int near = diff <= 0 ? node.Left : node.Right;
        int far = diff <= 0 ? node.Right : node.Left;

        Search(near, r, g, b, depth + 1, ref best, ref bestDist);

        // Only descend into the far subtree if a closer point could possibly exist there — i.e. the
        // splitting plane itself is closer than the current best match.
        if (diff * diff < bestDist)
        {
            Search(far, r, g, b, depth + 1, ref best, ref bestDist);
        }
    }
}
