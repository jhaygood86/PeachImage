using System.Numerics;
using PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// RGB to Y'CbCr for 10- and 12-bit sources (16-bit samples already scaled to the AV1 bit depth): the same full-range BT.601 matrix the 8-bit converter
/// uses, in float, with the chroma centered on <c>1 &lt;&lt; (bitDepth - 1)</c> and clamped to the depth's range. The luma/chroma arithmetic runs on
/// <see cref="Vector{T}"/> (256-bit with AVX2, 128-bit with NEON or SSE) one row at a time; the scalar twins the tests compare against, and the tails, are plain loops.
/// </summary>
internal static class Av1RgbToYuvHighDepth
{
    public static int[] ConvertMonoChrome(ReadOnlySpan<ushort> gray, int pixelCount)
    {
        var y = new int[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            y[i] = gray[i];
        }

        return y;
    }

    /// <summary>Lossless 4:4:4 identity matrix: Y = G, U = B, V = R, exactly (the high-depth counterpart of <see cref="Av1RgbToYuvIdentityConverter"/>).</summary>
    public static (int[] Y, int[] U, int[] V) ConvertIdentity(ReadOnlySpan<ushort> rgb, int pixelCount)
    {
        var y = new int[pixelCount];
        var u = new int[pixelCount];
        var v = new int[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            y[i] = rgb[(i * 3) + 1];
            u[i] = rgb[(i * 3) + 2];
            v[i] = rgb[i * 3];
        }

        return (y, u, v);
    }

    /// <summary>Lossy 4:2:0: full-resolution Y and chroma, then each chroma sample is the average of its 2x2 neighborhood (edge-replicated), like the 8-bit path.</summary>
    public static (int[] Y, int[] U, int[] V, int ChromaWidth, int ChromaHeight) Convert(ReadOnlySpan<ushort> rgb, int width, int height, int bitDepth)
    {
        var y = new int[width * height];
        var cbFull = new float[width * height];
        var crFull = new float[width * height];
        RgbToYuvFullRes(rgb, y, cbFull, crFull, width * height, bitDepth, vectorize: true);

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        int max = (1 << bitDepth) - 1;
        var u = new int[chromaWidth * chromaHeight];
        var v = new int[chromaWidth * chromaHeight];
        for (int cRow = 0; cRow < chromaHeight; cRow++)
        {
            int row0 = Math.Min(cRow * 2, height - 1);
            int row1 = Math.Min((cRow * 2) + 1, height - 1);
            for (int cCol = 0; cCol < chromaWidth; cCol++)
            {
                int col0 = Math.Min(cCol * 2, width - 1);
                int col1 = Math.Min((cCol * 2) + 1, width - 1);
                double uSum = cbFull[(row0 * width) + col0] + cbFull[(row0 * width) + col1] + cbFull[(row1 * width) + col0] + cbFull[(row1 * width) + col1];
                double vSum = crFull[(row0 * width) + col0] + crFull[(row0 * width) + col1] + crFull[(row1 * width) + col0] + crFull[(row1 * width) + col1];
                int cIdx = (cRow * chromaWidth) + cCol;
                u[cIdx] = Math.Clamp((int)Math.Round(uSum / 4.0), 0, max);
                v[cIdx] = Math.Clamp((int)Math.Round(vSum / 4.0), 0, max);
            }
        }

        return (y, u, v, chromaWidth, chromaHeight);
    }

    /// <summary>Full-resolution Y (rounded, clamped integers) and unrounded Cb/Cr for <paramref name="pixelCount"/> interleaved RGB pixels.</summary>
    internal static void RgbToYuvFullRes(ReadOnlySpan<ushort> rgb, Span<int> yOut, Span<float> cbOut, Span<float> crOut, int pixelCount, int bitDepth, bool vectorize)
    {
        float max = (1 << bitDepth) - 1;
        float mid = 1 << (bitDepth - 1);
        int i = 0;
        if (vectorize && Vector.IsHardwareAccelerated && pixelCount >= Vector<float>.Count)
        {
            int n = Vector<float>.Count;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var kr = new Vector<float>(Av1RgbToYuvCoefficients.Kr);
            var kg = new Vector<float>(Av1RgbToYuvCoefficients.Kg);
            var kb = new Vector<float>(Av1RgbToYuvCoefficients.Kb);
            var kbInvHalf = new Vector<float>(Av1RgbToYuvCoefficients.KbInvHalf);
            var krInvHalf = new Vector<float>(Av1RgbToYuvCoefficients.KrInvHalf);
            var vMid = new Vector<float>(mid);
            var vMax = new Vector<float>(max);
            var half = new Vector<float>(0.5f);
            for (; i <= pixelCount - n; i += n)
            {
                // De-interleave one vector's worth of pixels (stride 3 has no portable shuffle).
                for (int k = 0; k < n; k++)
                {
                    int p = (i + k) * 3;
                    r[k] = rgb[p];
                    g[k] = rgb[p + 1];
                    b[k] = rgb[p + 2];
                }

                var vr = new Vector<float>(r);
                var vg = new Vector<float>(g);
                var vb = new Vector<float>(b);
                var luma = (kr * vr) + (kg * vg) + (kb * vb);
                var yRounded = Vector.Min(Vector.Max(Vector.Floor(luma + half), Vector<float>.Zero), vMax);
                Vector.ConvertToInt32(yRounded).CopyTo(yOut[i..]);
                ((vb - luma) * kbInvHalf + vMid).CopyTo(cbOut[i..]);
                ((vr - luma) * krInvHalf + vMid).CopyTo(crOut[i..]);
            }
        }

        RgbToYuvFullResScalar(rgb[(i * 3)..], yOut[i..], cbOut[i..], crOut[i..], pixelCount - i, bitDepth);
    }

    internal static void RgbToYuvFullResScalar(ReadOnlySpan<ushort> rgb, Span<int> yOut, Span<float> cbOut, Span<float> crOut, int pixelCount, int bitDepth)
    {
        float max = (1 << bitDepth) - 1;
        float mid = 1 << (bitDepth - 1);
        for (int i = 0; i < pixelCount; i++)
        {
            float r = rgb[i * 3];
            float g = rgb[(i * 3) + 1];
            float b = rgb[(i * 3) + 2];
            float luma = (Av1RgbToYuvCoefficients.Kr * r) + (Av1RgbToYuvCoefficients.Kg * g) + (Av1RgbToYuvCoefficients.Kb * b);
            yOut[i] = (int)Math.Clamp(MathF.Floor(luma + 0.5f), 0f, max);
            cbOut[i] = ((b - luma) * Av1RgbToYuvCoefficients.KbInvHalf) + mid;
            crOut[i] = ((r - luma) * Av1RgbToYuvCoefficients.KrInvHalf) + mid;
        }
    }
}
