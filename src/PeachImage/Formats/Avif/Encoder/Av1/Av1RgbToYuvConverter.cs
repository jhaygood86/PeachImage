using PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Converts packed 8-bit RGB24 (or Gray8, for the monochrome path) pixels into planar Y(+U/V) samples for
/// AV1 encoding -- the forward-direction mirror of <see cref="Decoding.Av1.Av1YuvToRgbConverter"/>. This is
/// the 4:2:0/BT.601 path <see cref="Av1FrameEncoder.Encode"/> always uses for non-lossless encodes (full-range
/// BT.601, <c>matrix_coefficients = 6</c>, matching <see cref="Av1SequenceHeaderWriter.MatrixCoefficients"/>);
/// see <see cref="Av1RgbToYuvIdentityConverter"/> for the 4:4:4/identity-matrix path lossless encoding uses
/// instead, which needs no chroma subsampling and is exactly invertible (unlike BT.601). This converter's own
/// 4:2:0 chroma subsampling -- box-filter-averaging the full-resolution Cb/Cr samples in 2x2 groups
/// (edge-replicating the last row/column on odd dimensions) -- is algebraically the same operation
/// <see cref="Decoding.Av1.Av1YuvToRgbConverter"/> inverts, so encoding then decoding through PeachImage's
/// own AV1 decoder reproduces the source up to ordinary 8-bit rounding and the inherent loss of 4:2:0
/// chroma subsampling, before any quantization is even applied.
///
/// <para>Output planes are exactly <c>width</c> x <c>height</c> (luma) and
/// <c>ceil(width/2)</c> x <c>ceil(height/2)</c> (chroma) -- unpadded. Superblock/tile-boundary padding is
/// the tile encoder's concern (it already needs edge-replication for prediction at frame edges), not this
/// converter's.</para>
///
/// <para>The per-pixel BT.601 math (this is the single hottest per-pixel loop in the encoder, O(width x
/// height)) is delegated to <see cref="Av1RgbToYuvKernelSelector.Instance"/> -- a tiered SIMD kernel
/// following the same <c>IXxxKernel</c>/<c>XxxKernelSelector</c> shape as JPEG's
/// <see cref="Jpeg.ColorConversion.ColorConverterSelector"/>. Only the 4:2:0 box-filter chroma downsample
/// below stays a plain scalar loop: it runs over the much smaller chroma-resolution grid (1/4 the pixel
/// count), so it was not worth the extra kernel-tier surface area for the win available.</para>
/// </summary>
internal static class Av1RgbToYuvConverter
{
    /// <summary>Converts a monochrome (Gray8) source into a single Y plane -- exactly the source samples, since a true gray sample's Y projection is itself (Kr + Kg + Kb == 1).</summary>
    public static int[] ConvertMonoChrome(ReadOnlySpan<byte> gray, int width, int height)
    {
        var y = new int[width * height];
        Av1RgbToYuvKernelSelector.Instance.ConvertMonoChrome(gray, y, y.Length);
        return y;
    }

    /// <summary>Converts a packed RGB24 source into Y (full resolution) and U/V (4:2:0 subsampled) planes.</summary>
    public static (int[] Y, int[] U, int[] V, int ChromaWidth, int ChromaHeight) Convert(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var y = new int[width * height];
        var cbFull = new float[width * height];
        var crFull = new float[width * height];

        Av1RgbToYuvKernelSelector.Instance.RgbToYuvFullRes(rgb, y, cbFull, crFull, width * height);

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
