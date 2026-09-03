namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Converts packed 8-bit RGB24 pixels into planar Y/U/V samples using AV1's identity color matrix
/// (<c>matrix_coefficients = 0</c>, matching <see cref="Av1SequenceHeaderWriter.MatrixCoefficientsIdentity"/>):
/// <c>Y = G, U = B, V = R</c>, full resolution, no cross-channel math at all -- the forward-direction mirror
/// of <see cref="Decoding.Av1.Av1YuvToRgbConverter"/>'s own identity decode path. Unlike
/// <see cref="Av1RgbToYuvConverter"/>'s BT.601 conversion (which mixes channels via floating-point
/// coefficients and is not exactly invertible even without chroma subsampling), this is a pure channel
/// relabel: every sample round-trips through <c>Av1YuvToRgbConverter</c> bit-exact, with no rounding and no
/// chroma downsampling (output is already 4:4:4) -- exactly what real lossless AVIF encoders (libavif/aom
/// <c>--lossless</c>) use to achieve genuine pixel-exact output. Only used when encoding losslessly (see
/// <see cref="Av1FrameEncoder.Encode"/>'s <c>chroma444</c> gate); the lossy path keeps using
/// <see cref="Av1RgbToYuvConverter"/> unconditionally.
/// </summary>
internal static class Av1RgbToYuvIdentityConverter
{
    /// <summary>Converts a packed RGB24 source into full-resolution (4:4:4) Y/U/V planes: <c>Y = G, U = B, V = R</c>, exactly.</summary>
    public static (int[] Y, int[] U, int[] V) Convert(ReadOnlySpan<byte> rgb, int width, int height)
    {
        int count = width * height;
        var y = new int[count];
        var u = new int[count];
        var v = new int[count];

        for (int i = 0; i < count; i++)
        {
            int idx = i * 3;
            y[i] = rgb[idx + 1]; // G
            u[i] = rgb[idx + 2]; // B
            v[i] = rgb[idx]; // R
        }

        return (y, u, v);
    }
}
