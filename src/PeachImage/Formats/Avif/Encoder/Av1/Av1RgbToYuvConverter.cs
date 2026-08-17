namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Converts packed 8-bit RGB24 (or Gray8, for the monochrome path) pixels into planar Y(+U/V) samples for
/// AV1 encoding -- the forward-direction mirror of <see cref="Decoding.Av1.Av1YuvToRgbConverter"/>. Always
/// uses full-range BT.601 (<c>matrix_coefficients = 6</c>, matching <see cref="Av1SequenceHeaderWriter.MatrixCoefficients"/>)
/// since that's the only matrix/range combination this encoder ever signals in the sequence header, and
/// always chroma-subsamples 4:2:0 by box-filter-averaging the full-resolution Cb/Cr samples in 2x2 groups
/// (edge-replicating the last row/column on odd dimensions) -- algebraically the same operation
/// <see cref="Decoding.Av1.Av1YuvToRgbConverter"/> inverts, so encoding then decoding through PeachImage's
/// own AV1 decoder reproduces the source up to ordinary 8-bit rounding and the inherent loss of 4:2:0
/// chroma subsampling, before any quantization is even applied.
///
/// <para>Output planes are exactly <c>width</c> x <c>height</c> (luma) and
/// <c>ceil(width/2)</c> x <c>ceil(height/2)</c> (chroma) -- unpadded. Superblock/tile-boundary padding is
/// the tile encoder's concern (it already needs edge-replication for prediction at frame edges), not this
/// converter's.</para>
/// </summary>
internal static class Av1RgbToYuvConverter
{
    private const double Kr = 0.299;
    private const double Kb = 0.114;
    private const double Kg = 1.0 - Kr - Kb;

    // Full-range 8-bit constants, matching Av1YuvToRgbConverter's own yLo/yRange/cLo/cRange for
    // colorRangeFull == true, bitDepth == 8.
    private const double YRange = 255.0;
    private const double CLo = 128.0;
    private const double CRange = 255.0 / 2.0;

    /// <summary>Converts a monochrome (Gray8) source into a single Y plane -- exactly the source samples, since a true gray sample's Y projection is itself (Kr + Kg + Kb == 1).</summary>
    public static int[] ConvertMonoChrome(ReadOnlySpan<byte> gray, int width, int height)
    {
        var y = new int[width * height];
        for (int i = 0; i < y.Length; i++)
        {
            y[i] = gray[i];
        }

        return y;
    }

    /// <summary>Converts a packed RGB24 source into Y (full resolution) and U/V (4:2:0 subsampled) planes.</summary>
    public static (int[] Y, int[] U, int[] V, int ChromaWidth, int ChromaHeight) Convert(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var y = new int[width * height];
        var cbFull = new double[width * height];
        var crFull = new double[width * height];

        for (int row = 0; row < height; row++)
        {
            int rowBase = row * width;
            int srcRowBase = rowBase * 3;
            for (int col = 0; col < width; col++)
            {
                int srcIdx = srcRowBase + (col * 3);
                double rn = rgb[srcIdx] / 255.0;
                double gn = rgb[srcIdx + 1] / 255.0;
                double bn = rgb[srcIdx + 2] / 255.0;

                double yn = (Kr * rn) + (Kg * gn) + (Kb * bn);
                double crn = (rn - yn) / (2 * (1 - Kr));
                double cbn = (bn - yn) / (2 * (1 - Kb));

                int idx = rowBase + col;
                y[idx] = ClampToByte(yn * YRange);
                cbFull[idx] = (cbn * CRange) + CLo;
                crFull[idx] = (crn * CRange) + CLo;
            }
        }

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        var u = new int[chromaWidth * chromaHeight];
        var v = new int[chromaWidth * chromaHeight];

        for (int cRow = 0; cRow < chromaHeight; cRow++)
        {
            int row0 = Math.Min((cRow * 2) + 0, height - 1);
            int row1 = Math.Min((cRow * 2) + 1, height - 1);
            for (int cCol = 0; cCol < chromaWidth; cCol++)
            {
                int col0 = Math.Min((cCol * 2) + 0, width - 1);
                int col1 = Math.Min((cCol * 2) + 1, width - 1);

                double uSum = cbFull[(row0 * width) + col0] + cbFull[(row0 * width) + col1] + cbFull[(row1 * width) + col0] + cbFull[(row1 * width) + col1];
                double vSum = crFull[(row0 * width) + col0] + crFull[(row0 * width) + col1] + crFull[(row1 * width) + col0] + crFull[(row1 * width) + col1];

                int cIdx = (cRow * chromaWidth) + cCol;
                u[cIdx] = ClampToByte(uSum / 4.0);
                v[cIdx] = ClampToByte(vSum / 4.0);
            }
        }

        return (y, u, v, chromaWidth, chromaHeight);
    }

    private static int ClampToByte(double value) => Math.Clamp((int)Math.Round(value), 0, 255);
}
