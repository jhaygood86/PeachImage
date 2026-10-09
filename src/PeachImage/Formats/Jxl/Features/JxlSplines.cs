using System.Numerics;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jxl.Features;

/// <summary>
/// Splines: parametric curves whose colour and thickness vary along their length (stored as 32-term DCTs), drawn by
/// splatting Gaussians along a centripetal Catmull-Rom path and added to the XYB planes.
/// </summary>
internal sealed class JxlSplines
{
    private const int NumContexts = 6;
    private const int QuantizationAdjustmentContext = 0;
    private const int StartingPositionContext = 1;
    private const int NumSplinesContext = 2;
    private const int NumControlPointsContext = 3;
    private const int ControlPointsContext = 4;
    private const int DctContext = 5;

    private const int MaxControlPoints = 1 << 20;
    private const float DesiredRenderingDistance = 1f;
    private const float Sqrt2 = 1.41421356237f;
    private const float Sqrt0_5 = 0.70710678118f;
    private const long PositionLimit = 1L << 23;
    private static readonly float[] ChannelWeight = [0.0042f, 0.075f, 0.07f, 0.3333f];

    private readonly List<QuantizedSpline> _splines = [];
    private readonly List<Point> _startingPoints = [];
    private int _quantizationAdjustment;
    private SplineSegment[] _segments = [];

    public bool HasAny => _splines.Count > 0;

    private readonly record struct Point(float X, float Y);

    private struct SplineSegment
    {
        public float CenterX;
        public float CenterY;
        public float MaximumDistance;
        public float InvSigma;
        public float SigmaOver4TimesIntensity;
        public float Color0;
        public float Color1;
        public float Color2;
        public int YStart;
        public int YEnd;
    }

    private sealed class QuantizedSpline
    {
        public (long Dx, long Dy)[] ControlPoints = [];
        public int[][] ColorDct = [new int[32], new int[32], new int[32]];
        public int[] SigmaDct = new int[32];
    }

    private sealed class Spline
    {
        public List<Point> ControlPoints = [];
        public float[][] ColorDct = [new float[32], new float[32], new float[32]];
        public float[] SigmaDct = new float[32];
    }

    /// <summary>Reads the spline set of a frame's global section.</summary>
    public static JxlSplines Read(ref JxlBitReader br, long numPixels)
    {
        var code = JxlEntropyCode.Read(ref br, NumContexts, out byte[] contextMap);
        using var reader = new JxlSymbolReader(code, ref br);
        var result = new JxlSplines();

        long numSplines = reader.ReadHybridUint(NumSplinesContext, ref br, contextMap);
        long maxControlPoints = Math.Min(MaxControlPoints, numPixels / 2);
        if (numSplines > maxControlPoints || numSplines + 1 > maxControlPoints)
        {
            throw new JxlDecodingException("Too many splines.");
        }

        numSplines++;
        long lastX = 0;
        long lastY = 0;
        for (long i = 0; i < numSplines; i++)
        {
            uint dx = reader.ReadHybridUint(StartingPositionContext, ref br, contextMap);
            uint dy = reader.ReadHybridUint(StartingPositionContext, ref br, contextMap);
            long x;
            long y;
            if (i != 0)
            {
                x = JxlFieldReader.UnpackSigned(dx) + lastX;
                y = JxlFieldReader.UnpackSigned(dy) + lastY;
            }
            else
            {
                x = dx;
                y = dy;
            }

            ValidatePosition(x, y);
            result._startingPoints.Add(new Point(x, y));
            lastX = x;
            lastY = y;
        }

        result._quantizationAdjustment = JxlFieldReader.UnpackSigned(reader.ReadHybridUint(QuantizationAdjustmentContext, ref br, contextMap));

        long totalControlPoints = numSplines;
        for (long i = 0; i < numSplines; i++)
        {
            result._splines.Add(ReadSpline(ref br, reader, contextMap, maxControlPoints, ref totalControlPoints));
            br.ThrowIfOverrun();
        }

        if (!reader.CheckFinalState())
        {
            throw new JxlDecodingException("ANS final state check failed while decoding splines.");
        }

        return result;
    }

    private static QuantizedSpline ReadSpline(ref JxlBitReader br, JxlSymbolReader reader, byte[] contextMap, long maxControlPoints, ref long totalControlPoints)
    {
        var spline = new QuantizedSpline();
        long count = reader.ReadHybridUint(NumControlPointsContext, ref br, contextMap);
        if (count > maxControlPoints)
        {
            throw new JxlDecodingException("Too many spline control points.");
        }

        totalControlPoints += count;
        if (totalControlPoints > maxControlPoints)
        {
            throw new JxlDecodingException("Too many spline control points.");
        }

        const long deltaLimit = 1L << 30;
        spline.ControlPoints = new (long, long)[count];
        for (int i = 0; i < count; i++)
        {
            long dx = JxlFieldReader.UnpackSigned(reader.ReadHybridUint(ControlPointsContext, ref br, contextMap));
            long dy = JxlFieldReader.UnpackSigned(reader.ReadHybridUint(ControlPointsContext, ref br, contextMap));
            if (dx >= deltaLimit || dx <= -deltaLimit || dy >= deltaLimit || dy <= -deltaLimit)
            {
                throw new JxlDecodingException("A spline delta-delta is out of bounds.");
            }

            spline.ControlPoints[i] = (dx, dy);
        }

        foreach (int[] dct in spline.ColorDct)
        {
            ReadDct(ref br, reader, contextMap, dct);
        }

        ReadDct(ref br, reader, contextMap, spline.SigmaDct);
        return spline;
    }

    private static void ReadDct(ref JxlBitReader br, JxlSymbolReader reader, byte[] contextMap, int[] dct)
    {
        for (int i = 0; i < 32; i++)
        {
            dct[i] = JxlFieldReader.UnpackSigned(reader.ReadHybridUint(DctContext, ref br, contextMap));
            if (dct[i] == int.MinValue)
            {
                throw new JxlDecodingException("Invalid spline DCT value.");
            }
        }
    }

    private static void ValidatePosition(long x, long y)
    {
        if (x >= PositionLimit || x <= -PositionLimit || y >= PositionLimit || y <= -PositionLimit)
        {
            throw new JxlDecodingException("Spline coordinates are out of bounds.");
        }
    }

    private static float AdjustedQuantInverse(int adjustment) =>
        adjustment >= 0 ? 1f / (1f + (0.125f * adjustment)) : 1f - (0.125f * adjustment);

    /// <summary>Dequantizes the splines and precomputes the Gaussian segments to draw.</summary>
    public void Prepare(int width, int height, float yToX, float yToB)
    {
        var segments = new List<SplineSegment>();
        ulong totalArea = 0;
        long imageSize = (long)width * height;
        var splines = new List<Spline>(_splines.Count);
        for (int i = 0; i < _splines.Count; i++)
        {
            var spline = Dequantize(_splines[i], _startingPoints[i], yToX, yToB, imageSize, ref totalArea);
            for (int p = 1; p < spline.ControlPoints.Count; p++)
            {
                var a = spline.ControlPoints[p - 1];
                var b = spline.ControlPoints[p];
                if (Math.Abs(a.X - b.X) < 1e-3f && Math.Abs(a.Y - b.Y) < 1e-3f)
                {
                    throw new JxlDecodingException($"Identical successive control points in spline {i}.");
                }
            }

            splines.Add(spline);
        }

        Span<float> color = stackalloc float[3];
        foreach (var spline in splines)
        {
            var intermediate = CatmullRom(spline.ControlPoints);
            var pointsToDraw = new List<(Point Point, float Multiplier)>();
            ForEachEquallySpacedPoint(intermediate, (p, m) => pointsToDraw.Add((p, m)));
            float arcLength = ((pointsToDraw.Count - 2) * DesiredRenderingDistance) + pointsToDraw[^1].Multiplier;
            if (arcLength <= 0f)
            {
                continue;
            }

            float inverseArcLength = 1f / arcLength;
            int k = 0;
            foreach (var (point, multiplier) in pointsToDraw)
            {
                float progress = Math.Min(1f, k * DesiredRenderingDistance * inverseArcLength);
                k++;
                for (int c = 0; c < 3; c++)
                {
                    color[c] = ContinuousIdct(spline.ColorDct[c], 31 * progress);
                }

                float sigma = ContinuousIdct(spline.SigmaDct, 31 * progress);
                AddSegment(height, point, multiplier, color, sigma, segments);
            }
        }

        _segments = [.. segments];
    }

    private Spline Dequantize(QuantizedSpline q, Point start, float yToX, float yToB, long imageSize, ref ulong totalArea)
    {
        const ulong one = 1;
        ulong areaLimit = Math.Min((1024UL * (ulong)imageSize) + (one << 32), one << 42);
        var result = new Spline();
        int currentX = (int)MathF.Round(start.X);
        int currentY = (int)MathF.Round(start.Y);
        ValidatePosition(currentX, currentY);
        result.ControlPoints.Add(new Point(currentX, currentY));
        long deltaX = 0;
        long deltaY = 0;
        ulong manhattan = 0;
        foreach (var (dx, dy) in q.ControlPoints)
        {
            deltaX += dx;
            deltaY += dy;
            manhattan += (ulong)(Math.Abs(deltaX) + Math.Abs(deltaY));
            if (manhattan > areaLimit)
            {
                throw new JxlDecodingException("The splines are too large.");
            }

            ValidatePosition(deltaX, deltaY);
            currentX += (int)deltaX;
            currentY += (int)deltaY;
            ValidatePosition(currentX, currentY);
            result.ControlPoints.Add(new Point(currentX, currentY));
        }

        float invQuant = AdjustedQuantInverse(_quantizationAdjustment);
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 32; i++)
            {
                float invDctFactor = i == 0 ? Sqrt0_5 : 1f;
                result.ColorDct[c][i] = q.ColorDct[c][i] * invDctFactor * ChannelWeight[c] * invQuant;
            }
        }

        for (int i = 0; i < 32; i++)
        {
            result.ColorDct[0][i] += yToX * result.ColorDct[1][i];
            result.ColorDct[2][i] += yToB * result.ColorDct[1][i];
        }

        var color = new ulong[3];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 32; i++)
            {
                color[c] += (ulong)MathF.Ceiling(invQuant * Math.Abs(q.ColorDct[c][i]));
            }
        }

        color[0] += (ulong)MathF.Ceiling(Math.Abs(yToX)) * color[1];
        color[2] += (ulong)MathF.Ceiling(Math.Abs(yToB)) * color[1];
        ulong maxColor = Math.Max(color[1], Math.Max(color[0], color[2]));
        ulong logColor = Math.Max(one, (ulong)CeilLog2(one + maxColor));
        float weightLimit = MathF.Ceiling(MathF.Sqrt(((float)areaLimit / logColor) / Math.Max(1UL, manhattan)));

        ulong widthEstimate = 0;
        for (int i = 0; i < 32; i++)
        {
            float invDctFactor = i == 0 ? Sqrt0_5 : 1f;
            result.SigmaDct[i] = q.SigmaDct[i] * invDctFactor * ChannelWeight[3] * invQuant;
            float weightF = MathF.Ceiling(invQuant * Math.Abs(q.SigmaDct[i]));
            ulong weight = (ulong)Math.Min(weightLimit, Math.Max(1f, weightF));
            widthEstimate += weight * weight * logColor;
        }

        totalArea += widthEstimate * manhattan;
        if (totalArea > areaLimit)
        {
            throw new JxlDecodingException("The splines cover too large an area.");
        }

        return result;
    }

    private static int CeilLog2(ulong value) => value <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(value - 1);

    private static List<Point> CatmullRom(List<Point> controlPoints)
    {
        var result = new List<Point>();
        if (controlPoints.Count == 0)
        {
            return result;
        }

        if (controlPoints.Count == 1)
        {
            result.Add(controlPoints[0]);
            return result;
        }

        const int numPoints = 16;
        var points = new List<Point>(controlPoints);
        points.Insert(0, Add(points[0], Sub(points[0], points[1])));
        points.Add(Add(points[^1], Sub(points[^1], points[^2])));
        Span<float> d = stackalloc float[3];
        Span<float> t = stackalloc float[4];
        Span<Point> a = stackalloc Point[3];
        Span<Point> b = stackalloc Point[2];
        for (int start = 0; start < points.Count - 3; start++)
        {
            result.Add(points[start + 1]);
            t[0] = 0;
            for (int k = 0; k < 3; k++)
            {
                var p0 = points[start + k];
                var p1 = points[start + k + 1];
                d[k] = MathF.Sqrt(Hypot(p1.X - p0.X, p1.Y - p0.Y));
                t[k + 1] = t[k] + d[k];
            }

            for (int i = 1; i < numPoints; i++)
            {
                float tt = d[0] + ((float)i / numPoints * d[1]);
                for (int k = 0; k < 3; k++)
                {
                    var pk = points[start + k];
                    a[k] = Add(pk, Scale((tt - t[k]) / d[k], Sub(points[start + k + 1], pk)));
                }

                for (int k = 0; k < 2; k++)
                {
                    b[k] = Add(a[k], Scale((tt - t[k]) / (d[k] + d[k + 1]), Sub(a[k + 1], a[k])));
                }

                result.Add(Add(b[0], Scale((tt - t[1]) / d[1], Sub(b[1], b[0]))));
            }
        }

        result.Add(points[^2]);
        return result;
    }

    private static float Hypot(float x, float y) => (float)Math.Sqrt(((double)x * x) + ((double)y * y));

    private static Point Add(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);

    private static Point Sub(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    private static Point Scale(float k, Point p) => new(k * p.X, k * p.Y);

    private static void ForEachEquallySpacedPoint(List<Point> points, Action<Point, float> functor)
    {
        var current = points[0];
        functor(current, DesiredRenderingDistance);
        int next = 0;
        while (next < points.Count)
        {
            var previous = current;
            float arclengthFromPrevious = 0f;
            for (; ; )
            {
                if (next == points.Count)
                {
                    functor(previous, arclengthFromPrevious);
                    return;
                }

                var delta = Sub(points[next], previous);
                float arclengthToNext = MathF.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
                if (arclengthFromPrevious + arclengthToNext >= DesiredRenderingDistance)
                {
                    current = Add(previous, Scale((DesiredRenderingDistance - arclengthFromPrevious) / arclengthToNext, delta));
                    functor(current, DesiredRenderingDistance);
                    break;
                }

                arclengthFromPrevious += arclengthToNext;
                previous = points[next];
                next++;
            }
        }
    }

    // The 32-point DCT-3 of `dct` evaluated at t (a continuous position), scaled so that {x, 0, ..., 0} gives x everywhere.
    private static float ContinuousIdct(float[] dct, float t)
    {
        float tAndHalf = t + 0.5f;
        float result = 0;
        for (int i = 0; i < 32; i++)
        {
            float cos = FastCos((MathF.PI / 32 * i) * tAndHalf);
            result = (Sqrt2 * (dct[i] * cos)) + result;
        }

        return result;
    }

    private static void AddSegment(int imageHeight, Point center, float intensity, ReadOnlySpan<float> color, float sigma, List<SplineSegment> segments)
    {
        if (!(float.IsFinite(sigma) && sigma != 0f && float.IsFinite(1f / sigma) && float.IsFinite(intensity)))
        {
            return;
        }

        const float distanceExp = 5f;
        float maxColor = 0.01f;
        for (int c = 0; c < 3; c++)
        {
            maxColor = Math.Max(maxColor, Math.Abs(color[c] * intensity));
        }

        float maximumDistance = MathF.Sqrt(-2f * sigma * sigma * ((MathF.Log(0.1f) * distanceExp) - MathF.Log(maxColor)));
        long y0 = Math.Max(0L, RoundAway(center.Y - maximumDistance));
        long y1 = Math.Min(imageHeight, RoundAway(center.Y + maximumDistance) + 1);
        if (y1 <= y0)
        {
            return;
        }

        segments.Add(new SplineSegment
        {
            CenterX = center.X,
            CenterY = center.Y,
            MaximumDistance = maximumDistance,
            InvSigma = 1f / sigma,
            SigmaOver4TimesIntensity = 0.25f * sigma * intensity,
            Color0 = color[0],
            Color1 = color[1],
            Color2 = color[2],
            YStart = (int)y0,
            YEnd = (int)y1,
        });
    }

    private static long RoundAway(float v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Adds the splines to the three XYB planes in place. Rows are independent, so they are drawn on all cores; each row adds its
    /// segments in their original order, which keeps the floating-point sums identical to a sequential pass.
    /// </summary>
    public void AddTo(float[][] planes, int stride, int width, int height, bool vectorized = true)
    {
        if (_segments.Length == 0 || width <= 0)
        {
            return;
        }

        // Per row, the segments that reach it (counting sort, so segment order is preserved within a row).
        var spanStart = new int[_segments.Length];
        var spanEnd = new int[_segments.Length];
        var rowStart = new int[height + 1];
        for (int i = 0; i < _segments.Length; i++)
        {
            ref readonly var segment = ref _segments[i];
            long start = RoundAway(segment.CenterX - segment.MaximumDistance);
            long end = RoundAway(segment.CenterX + segment.MaximumDistance);
            if (end < 0 || start >= width)
            {
                spanStart[i] = spanEnd[i] = 0;
                continue;
            }

            spanStart[i] = (int)Math.Max(0, start);
            spanEnd[i] = (int)Math.Min(width, end + 1);
            for (int y = segment.YStart; y < Math.Min(segment.YEnd, height); y++)
            {
                rowStart[y + 1]++;
            }
        }

        for (int y = 0; y < height; y++)
        {
            rowStart[y + 1] += rowStart[y];
        }

        var fill = (int[])rowStart.Clone();
        var indices = new int[rowStart[height]];
        for (int i = 0; i < _segments.Length; i++)
        {
            if (spanEnd[i] <= spanStart[i])
            {
                continue;
            }

            for (int y = _segments[i].YStart; y < Math.Min(_segments[i].YEnd, height); y++)
            {
                indices[fill[y]++] = i;
            }
        }

        RowParallel.For(height, y =>
        {
            for (int k = rowStart[y]; k < rowStart[y + 1]; k++)
            {
                int i = indices[k];
                DrawRow(in _segments[i], y, spanStart[i], spanEnd[i], planes, y * stride, vectorized);
            }
        });
    }

    private static void DrawRow(in SplineSegment segment, int y, int x0, int x1, float[][] planes, int row, bool vectorized)
    {
        float dy = y - segment.CenterY;
        float dy2 = dy * dy;
        int x = x0;
        if (vectorized && Vector.IsHardwareAccelerated && x1 - x0 >= Vector<float>.Count)
        {
            int n = Vector<float>.Count;
            var iota = new Vector<float>(Iota);
            var centerX = new Vector<float>(segment.CenterX);
            var half = new Vector<float>(0.5f);
            var offset = new Vector<float>(0.353553391f);
            var invSigma = new Vector<float>(segment.InvSigma);
            var scale = new Vector<float>(segment.SigmaOver4TimesIntensity);
            var vdy2 = new Vector<float>(dy2);
            var color0 = new Vector<float>(segment.Color0);
            var color1 = new Vector<float>(segment.Color1);
            var color2 = new Vector<float>(segment.Color2);
            for (; x + n <= x1; x += n)
            {
                var dx = (new Vector<float>(x) + iota) - centerX;
                var distance = Vector.SquareRoot((dx * dx) + vdy2);
                var half_d = distance * half;
                var diff = FastErf((half_d + offset) * invSigma) - FastErf((half_d - offset) * invSigma);
                var local = scale * (diff * diff);
                int index = row + x;
                ((color0 * local) + new Vector<float>(planes[0], index)).CopyTo(planes[0], index);
                ((color1 * local) + new Vector<float>(planes[1], index)).CopyTo(planes[1], index);
                ((color2 * local) + new Vector<float>(planes[2], index)).CopyTo(planes[2], index);
            }
        }

        for (; x < x1; x++)
        {
            float dx = x - segment.CenterX;
            float distance = MathF.Sqrt((dx * dx) + dy2);
            float oneDimensional = FastErf(((distance * 0.5f) + 0.353553391f) * segment.InvSigma)
                - FastErf(((distance * 0.5f) - 0.353553391f) * segment.InvSigma);
            float local = segment.SigmaOver4TimesIntensity * (oneDimensional * oneDimensional);
            int index = row + x;
            planes[0][index] = (segment.Color0 * local) + planes[0][index];
            planes[1][index] = (segment.Color1 * local) + planes[1][index];
            planes[2][index] = (segment.Color2 * local) + planes[2][index];
        }
    }

    private static readonly float[] Iota = CreateIota();

    private static float[] CreateIota()
    {
        var iota = new float[Vector<float>.Count];
        for (int i = 0; i < iota.Length; i++)
        {
            iota[i] = i;
        }

        return iota;
    }

    private static Vector<float> FastErf(Vector<float> x)
    {
        var absx = Vector.Abs(x);
        var denom1 = (absx * new Vector<float>(7.77394369e-02f)) + new Vector<float>(2.05260015e-04f);
        var denom2 = (denom1 * absx) + new Vector<float>(2.32120216e-01f);
        var denom3 = (denom2 * absx) + new Vector<float>(2.77820801e-01f);
        var denom4 = (denom3 * absx) + Vector<float>.One;
        var denom5 = denom4 * denom4;
        var inv = Vector<float>.One / denom5;
        var result = Vector<float>.One - (inv * inv);
        return Vector.ConditionalSelect(Vector.LessThanOrEqual(x, Vector<float>.Zero), -result, result);
    }

    // Polynomial cosine approximation (L1 error 7e-5), matching the reference decoder's.
    private static float FastCos(float x)
    {
        const float pi = MathF.PI;
        const float pi2 = pi * 2.0f;
        float npi2 = MathF.Floor(x * (0.5f / pi)) * pi2;
        float xmodpi2 = x - npi2;
        float xpi = Math.Min(xmodpi2, pi2 - xmodpi2);
        bool abovePiHalf = xpi >= pi / 2.0f;
        float xpihalf = abovePiHalf ? pi - xpi : xpi;
        float xs = xpihalf * 0.25f;
        float x2 = xs * xs;
        float x4 = x2 * x2;
        float cosx = (x4 * 0.06960438f) + ((x2 * -0.84087373f) + 1.68179268f);
        float scale1 = (cosx * cosx) + -1.414213562f;
        float scale2 = (scale1 * scale1) + -1f;
        return abovePiHalf ? -scale2 : scale2;
    }

    // Rational error-function approximation (L1 error 7e-4), matching the reference decoder's.
    private static float FastErf(float x)
    {
        float absx = Math.Abs(x);
        float denom1 = (absx * 7.77394369e-02f) + 2.05260015e-04f;
        float denom2 = (denom1 * absx) + 2.32120216e-01f;
        float denom3 = (denom2 * absx) + 2.77820801e-01f;
        float denom4 = (denom3 * absx) + 1.0f;
        float denom5 = denom4 * denom4;
        float inv = 1.0f / denom5;
        float result = 1.0f - (inv * inv);
        return x <= 0 ? -result : result;
    }
}
