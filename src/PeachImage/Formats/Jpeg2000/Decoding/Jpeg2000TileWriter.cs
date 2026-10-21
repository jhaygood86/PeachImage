using System.Numerics;

namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>Colour transform, level shift and clamping of a decoded tile into the image's component planes.</summary>
internal static class J2kTileWriter
{
    /// <summary>Applies the multiple-component transform (if declared) and stores every component of the tile into <paramref name="planes"/>.</summary>
    public static void Write(J2kTileDecoder tile, J2kSiz siz, int reduce, ushort[][] planes, int[] planeWidths, int[] planeOriginX, int[] planeOriginY)
    {
        var components = tile.Components;
        int keepLevels = components.Min(c => c.Coding.Levels) >= reduce ? reduce : components.Min(c => c.Coding.Levels);

        if (tile.UsesMultipleComponentTransform && components.Length >= 3)
        {
            var a = components[0];
            var b = components[1];
            var c = components[2];
            int widthA = a.Resolutions[a.ResolutionCount - 1 - keepLevels].Width;
            int heightA = a.Resolutions[a.ResolutionCount - 1 - keepLevels].Height;
            bool sameSize = SameSize(b, widthA, heightA, keepLevels) && SameSize(c, widthA, heightA, keepLevels);
            if (sameSize)
            {
                int count = widthA * heightA;
                if (a.IntegerData is not null && b.IntegerData is not null && c.IntegerData is not null)
                {
                    InverseReversibleTransform(a.IntegerData, b.IntegerData, c.IntegerData, count);
                }
                else if (a.FloatData is not null && b.FloatData is not null && c.FloatData is not null)
                {
                    InverseIrreversibleTransform(a.FloatData, b.FloatData, c.FloatData, count);
                }
            }
        }

        for (int index = 0; index < components.Length; index++)
        {
            var component = components[index];
            var info = siz.Components[index];
            var top = component.Resolutions[component.ResolutionCount - 1 - keepLevels];
            int width = top.Width;
            int height = top.Height;
            int shift = 1 << (info.Depth - 1);
            int max = (1 << info.Depth) - 1;
            for (int y = 0; y < height; y++)
            {
                var destination = planes[index].AsSpan((((top.Y0 - planeOriginY[index]) + y) * planeWidths[index]) + (top.X0 - planeOriginX[index]), width);
                if (component.IntegerData is { } integers)
                {
                    StoreIntegers(integers.AsSpan(y * width, width), destination, shift, max);
                }
                else
                {
                    StoreFloats(component.FloatData!.AsSpan(y * width, width), destination, shift, max);
                }
            }
        }
    }

    private static bool SameSize(J2kTileComponent component, int width, int height, int keepLevels)
    {
        var top = component.Resolutions[component.ResolutionCount - 1 - keepLevels];
        return top.Width == width && top.Height == height;
    }

    private static void InverseReversibleTransform(int[] y0, int[] y1, int[] y2, int count)
    {
        int i = 0;
        int lanes = Vector<int>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + lanes <= count; i += lanes)
            {
                var v0 = new Vector<int>(y0, i);
                var v1 = new Vector<int>(y1, i);
                var v2 = new Vector<int>(y2, i);
                var g = v0 - Vector.ShiftRightArithmetic(v1 + v2, 2);
                (v2 + g).CopyTo(y0, i);
                g.CopyTo(y1, i);
                (v1 + g).CopyTo(y2, i);
            }
        }

        for (; i < count; i++)
        {
            int g = y0[i] - ((y1[i] + y2[i]) >> 2);
            int r = y2[i] + g;
            int b = y1[i] + g;
            y0[i] = r;
            y1[i] = g;
            y2[i] = b;
        }
    }

    private static void InverseIrreversibleTransform(float[] y0, float[] y1, float[] y2, int count)
    {
        int i = 0;
        int lanes = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + lanes <= count; i += lanes)
            {
                var v0 = new Vector<float>(y0, i);
                var v1 = new Vector<float>(y1, i);
                var v2 = new Vector<float>(y2, i);
                (v0 + (v2 * 1.402f)).CopyTo(y0, i);
                (v0 - (v1 * 0.34413f) - (v2 * 0.71414f)).CopyTo(y1, i);
                (v0 + (v1 * 1.772f)).CopyTo(y2, i);
            }
        }

        for (; i < count; i++)
        {
            float a = y0[i];
            float b = y1[i];
            float c = y2[i];
            y0[i] = a + (1.402f * c);
            y1[i] = a - (0.34413f * b) - (0.71414f * c);
            y2[i] = a + (1.772f * b);
        }
    }

    private static void StoreIntegers(ReadOnlySpan<int> source, Span<ushort> destination, int shift, int max)
    {
        int i = 0;
        int lanes = Vector<int>.Count;
        if (Vector.IsHardwareAccelerated && source.Length >= 2 * lanes)
        {
            var vShift = new Vector<int>(shift);
            var vMax = new Vector<int>(max);
            for (; i + (2 * lanes) <= source.Length; i += 2 * lanes)
            {
                var lo = Vector.Min(Vector.Max(new Vector<int>(source.Slice(i)) + vShift, Vector<int>.Zero), vMax);
                var hi = Vector.Min(Vector.Max(new Vector<int>(source.Slice(i + lanes)) + vShift, Vector<int>.Zero), vMax);
                Vector.Narrow(Vector.AsVectorUInt32(lo), Vector.AsVectorUInt32(hi)).CopyTo(destination.Slice(i));
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = (ushort)Math.Clamp(source[i] + shift, 0, max);
        }
    }

    private static void StoreFloats(ReadOnlySpan<float> source, Span<ushort> destination, int shift, int max)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = (ushort)Math.Clamp((int)MathF.Round(source[i]) + shift, 0, max);
        }
    }
}
