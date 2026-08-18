namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Studio/limited-range fixed-point RGB-&gt;YUV conversion (BT.601) plus 4:2:0 chroma downsampling — the
/// encode-side counterpart of <see cref="Decoding.Vp8.ColorConversion.Vp8ScalarColorConverter"/>. Transcribed
/// verbatim from libwebp's <c>src/dsp/yuv.h</c> (<c>VP8RGBToY</c>/<c>VP8RGBToU</c>/<c>VP8RGBToV</c>/
/// <c>VP8ClipUV</c>), cross-checked against the downloaded upstream source, with a plain round-to-nearest bias
/// in place of libwebp's optional dithering (a quality refinement, not a correctness requirement for a v1
/// lossy encoder).
/// </summary>
internal static class Vp8ForwardColorConverter
{
    private const int Fix = 16;
    private const int YRounding = 1 << (Fix - 1);
    private const int UvRounding = 1 << (Fix + 2 - 1);

    /// <summary>Converts one RGB sample to its Y value.</summary>
    public static byte ConvertY(int r, int g, int b)
    {
        int luma = (16839 * r) + (33059 * g) + (6420 * b);
        int y = (luma + YRounding + (16 << Fix)) >> Fix;
        return ClipByte(y);
    }

    /// <summary>
    /// Converts an accumulated (summed, not averaged) 2x2 block of RGB samples to its U value. The sum, not the
    /// average, is expected: <see cref="ClipUv"/>'s final &gt;&gt;(FIX+2) shift folds the divide-by-4 for the
    /// 2x2 block together with the fixed-point descale, matching libwebp's own accumulate-then-convert usage
    /// (<c>WebPAccumulateRGB</c> feeding <c>VP8RGBToU</c> directly, without a separate averaging step first).
    /// </summary>
    public static byte ConvertU(int r, int g, int b)
    {
        int u = (-9719 * r) - (19081 * g) + (28800 * b);
        return ClipUv(u);
    }

    /// <summary>Converts an accumulated (summed, not averaged) 2x2 block of RGB samples to its V value — see <see cref="ConvertU"/>'s remarks.</summary>
    public static byte ConvertV(int r, int g, int b)
    {
        int v = (28800 * r) - (24116 * g) - (4684 * b);
        return ClipUv(v);
    }

    private static byte ClipUv(int uv)
    {
        int result = (uv + UvRounding + (128 << (Fix + 2))) >> (Fix + 2);
        return ClipByte(result);
    }

    private static byte ClipByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>
    /// Converts an RGB24 image (<paramref name="width"/> x <paramref name="height"/>, row-major, 3 bytes per
    /// pixel) into a full-resolution Y plane and 4:2:0-subsampled U/V planes
    /// (<c>ceil(width/2)</c> x <c>ceil(height/2)</c>). Chroma is computed by summing each 2x2 RGB block
    /// (replicating the last row/column when width/height are odd, matching this codebase's clamp-to-edge
    /// convention elsewhere) and converting that sum once per block via <see cref="ConvertU"/>/<see cref="ConvertV"/>
    /// — matching libwebp's own accumulate-then-convert approach, rather than averaging already-converted
    /// per-pixel U/V samples. <paramref name="yStride"/>/<paramref name="chromaStride"/> are the destination
    /// planes' row strides, which may be larger than <paramref name="width"/>/the real chroma width (e.g. when
    /// writing into the top-left corner of a macroblock-grid-padded buffer the caller pads separately) — only
    /// the real <paramref name="width"/> x <paramref name="height"/> region (and its real, not padded, chroma
    /// extent) is ever read from <paramref name="rgb"/> or written here.
    /// </summary>
    public static void ConvertPlanes(ReadOnlySpan<byte> rgb, int width, int height, Span<byte> yPlane, int yStride, Span<byte> uPlane, Span<byte> vPlane, int chromaStride)
    {
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 3;
            int yRowBase = y * yStride;
            for (int x = 0; x < width; x++)
            {
                int o = rowBase + (x * 3);
                yPlane[yRowBase + x] = ConvertY(rgb[o], rgb[o + 1], rgb[o + 2]);
            }
        }

        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        for (int cy = 0; cy < chromaHeight; cy++)
        {
            int y0 = cy * 2;
            int y1 = Math.Min(y0 + 1, height - 1);
            int uvRowBase = cy * chromaStride;

            for (int cx = 0; cx < chromaWidth; cx++)
            {
                int x0 = cx * 2;
                int x1 = Math.Min(x0 + 1, width - 1);

                int o00 = (y0 * width * 3) + (x0 * 3);
                int o01 = (y0 * width * 3) + (x1 * 3);
                int o10 = (y1 * width * 3) + (x0 * 3);
                int o11 = (y1 * width * 3) + (x1 * 3);

                // Passed as a raw sum, not an average -- see ConvertU's remarks.
                int r = rgb[o00 + 0] + rgb[o01 + 0] + rgb[o10 + 0] + rgb[o11 + 0];
                int g = rgb[o00 + 1] + rgb[o01 + 1] + rgb[o10 + 1] + rgb[o11 + 1];
                int b = rgb[o00 + 2] + rgb[o01 + 2] + rgb[o10 + 2] + rgb[o11 + 2];

                uPlane[uvRowBase + cx] = ConvertU(r, g, b);
                vPlane[uvRowBase + cx] = ConvertV(r, g, b);
            }
        }
    }
}
