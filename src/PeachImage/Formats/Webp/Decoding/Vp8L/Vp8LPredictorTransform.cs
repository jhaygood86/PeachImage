using System.Runtime.InteropServices;
using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Inverts VP8L's predictor (spatial) transform: per tile (side <c>1 &lt;&lt; transform.Bits</c>), one of 14
/// predictor modes (0-13, selected via the tile parameter sub-image's green channel) forecasts each pixel
/// from its already-reconstructed left/top/top-left/top-right neighbors; the encoded residual is then added
/// back, per channel, mod 256. Two positions are unconditionally overridden regardless of what the tile
/// image says (matching libwebp's <c>PredictorInverseTransform_C</c> exactly): pixel (0,0) always uses mode 0
/// (fixed opaque black), and every other pixel in row 0 always uses mode 1 (left) — there is no row above
/// image row 0 for any tile-selected mode to sensibly reference.
/// </summary>
/// <remarks>
/// All neighbor reads use flat, unclamped <c>index +/- 1 +/- width</c> arithmetic against the single flat
/// pixel buffer, deliberately including one quirk this must reproduce exactly to match the reference decoder:
/// at the last column of a row, the "top-right" neighbor read (<c>index - width + 1</c>) is one past the end
/// of the row above, which — because the buffer is contiguous row-major memory — actually lands on the
/// *current* row's own first pixel (already reconstructed by the time it's read, since column 0 of every row
/// is always handled before the rest of that row). This is intentional per the VP8L spec/libwebp, not a bug
/// to guard against; both encoder and decoder rely on it.
/// </remarks>
internal static class Vp8LPredictorTransform
{
    public static void ApplyInverse(Span<uint> pixels, Vp8LTransform transform)
    {
        int width = transform.Xsize;
        int height = transform.Ysize;
        int bits = transform.Bits;
        var tileData = transform.Data!;
        int tilesPerRow = Vp8LMetaHuffmanImage.SubSampleSize(width, bits);
        var kernel = Vp8LTransformKernelSelector.Instance;

        // Pixel (0,0): mode 0 (fixed opaque black), unconditionally.
        pixels[0] = Vp8LPixelMath.AddWrapping(pixels[0], 0xFF000000u);

        // Rest of row 0: mode 1 (left), unconditionally -- sequential, has an intra-row dependency.
        for (int x = 1; x < width; x++)
        {
            pixels[x] = Vp8LPixelMath.AddWrapping(pixels[x], pixels[x - 1]);
        }

        for (int y = 1; y < height; y++)
        {
            int rowBase = y * width;

            // First pixel of the row: mode 2 (top), unconditionally.
            pixels[rowBase] = Vp8LPixelMath.AddWrapping(pixels[rowBase], pixels[rowBase - width]);

            int x = 1;
            while (x < width)
            {
                int tileX = x >> bits;
                int mode = (int)((tileData[((y >> bits) * tilesPerRow) + tileX] >> 8) & 0xF);
                int tileEndX = Math.Min((tileX + 1) << bits, width);
                int runLength = tileEndX - x;

                ApplyRun(pixels, kernel, width, rowBase, x, runLength, mode);
                x = tileEndX;
            }
        }
    }

    private static void ApplyRun(Span<uint> pixels, IVp8LTransformKernel kernel, int width, int rowBase, int xStart, int runLength, int mode)
    {
        if (mode == 2)
        {
            // Mode 2 (pure "top") has no intra-run dependency at all -- vectorizable, same operation as
            // Png.Filtering.VectorizedRowFilter.UnfilterUp applied to this run's raw ARGB bytes.
            var rowBytes = MemoryMarshal.Cast<uint, byte>(pixels.Slice(rowBase + xStart, runLength));
            var topBytes = MemoryMarshal.Cast<uint, byte>(pixels.Slice(rowBase - width + xStart, runLength));
            kernel.PredictorTopInverse(rowBytes, topBytes);
            return;
        }

        for (int i = 0; i < runLength; i++)
        {
            int index = rowBase + xStart + i;
            uint predicted = Predict(mode, pixels, index, width);
            pixels[index] = Vp8LPixelMath.AddWrapping(pixels[index], predicted);
        }
    }

    private static uint Predict(int mode, ReadOnlySpan<uint> pixels, int index, int width)
    {
        uint left = pixels[index - 1];
        uint top = pixels[index - width];

        switch (mode)
        {
            case 0: return 0xFF000000u;
            case 1: return left;
            case 2: return top;
        }

        uint topLeft = pixels[index - width - 1];
        uint topRight = pixels[index - width + 1];

        return mode switch
        {
            3 => topRight,
            4 => topLeft,
            5 => Average3(left, top, topRight),
            6 => Average2(left, topLeft),
            7 => Average2(left, top),
            8 => Average2(topLeft, top),
            9 => Average2(top, topRight),
            10 => Average4(left, topLeft, top, topRight),
            11 => Select(top, left, topLeft),
            12 => ClampedAddSubtractFull(left, top, topLeft),
            13 => ClampedAddSubtractHalf(left, top, topLeft),

            // Modes 14/15 are not assigned any meaning by the spec; a corrupt/hostile tile image could still
            // produce them (the field is 4 raw bits). Fall back to mode 0 rather than throw mid-pixel-loop,
            // consistent with this decoder's "stop early/degrade gracefully on malformed data" convention.
            _ => 0xFF000000u,
        };
    }

    private static uint Average2(uint a0, uint a1) => (((a0 ^ a1) & 0xFEFEFEFEu) >> 1) + (a0 & a1);

    private static uint Average3(uint a0, uint a1, uint a2) => Average2(Average2(a0, a2), a1);

    private static uint Average4(uint a0, uint a1, uint a2, uint a3) => Average2(Average2(a0, a1), Average2(a2, a3));

    private static uint Select(uint a, uint b, uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
            Sub3((int)((a >> 16) & 0xFF), (int)((b >> 16) & 0xFF), (int)((c >> 16) & 0xFF)) +
            Sub3((int)((a >> 8) & 0xFF), (int)((b >> 8) & 0xFF), (int)((c >> 8) & 0xFF)) +
            Sub3((int)(a & 0xFF), (int)(b & 0xFF), (int)(c & 0xFF));

        return paMinusPb <= 0 ? a : b;
    }

    private static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        int a = AddSubtractComponentFull((int)(c0 >> 24), (int)(c1 >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentFull((int)((c0 >> 16) & 0xFF), (int)((c1 >> 16) & 0xFF), (int)((c2 >> 16) & 0xFF));
        int g = AddSubtractComponentFull((int)((c0 >> 8) & 0xFF), (int)((c1 >> 8) & 0xFF), (int)((c2 >> 8) & 0xFF));
        int b = AddSubtractComponentFull((int)(c0 & 0xFF), (int)(c1 & 0xFF), (int)(c2 & 0xFF));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int AddSubtractComponentFull(int a, int b, int c) => Clip255(a + b - c);

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        uint average = Average2(c0, c1);
        int a = AddSubtractComponentHalf((int)(average >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentHalf((int)((average >> 16) & 0xFF), (int)((c2 >> 16) & 0xFF));
        int g = AddSubtractComponentHalf((int)((average >> 8) & 0xFF), (int)((c2 >> 8) & 0xFF));
        int b = AddSubtractComponentHalf((int)(average & 0xFF), (int)(c2 & 0xFF));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int AddSubtractComponentHalf(int a, int b) => Clip255(a + ((a - b) / 2));

    private static int Clip255(int value) => Math.Clamp(value, 0, 255);
}
