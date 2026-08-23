using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Converts decoded/composited AV1 Y(+U/V) planes, plus an optional decoded alpha plane, into a packed
/// pixel buffer honoring the AV1 bitstream's own <c>matrix_coefficients</c>/<c>color_range</c> (spec
/// §6.4.2) rather than assuming a fixed color space -- a deliberate departure from this repo's WebP codec,
/// whose VP8 decoder hard-codes studio-range YUV and ignores VP8's own (rarely-populated) color-space
/// bits. Chroma is nearest-neighbor upsampled for 4:2:0/4:2:2 -- a scalar-first approximation (matching
/// this repo's usual "scalar first, vectorize/refine later" staging), not a bilinear or
/// <c>chroma_sample_position</c>-aware reconstruction. Decoupled from <see cref="Av1FrameDecodeResult"/> so
/// the same conversion serves both a single decoded item and a grid-composited one.
/// </summary>
internal static class Av1YuvToRgbConverter
{
    /// <summary>
    /// Converts <paramref name="colorPlanes"/> (1 plane if <paramref name="monoChrome"/>, otherwise 3:
    /// Y, U, V) and an optional <paramref name="alphaPlane"/> (always a single plane, matching
    /// <paramref name="outWidth"/> x <paramref name="outHeight"/>) into a packed buffer. Output samples
    /// are 8-bit when <paramref name="bitDepth"/> is 8, otherwise 16-bit (native-endian <c>ushort</c>,
    /// matching this repo's existing <c>Rgb48</c>/<c>Rgba64</c>/<c>Gray16</c> convention -- see
    /// <c>PngImageDecoder</c>'s <c>MemoryMarshal.Cast&lt;byte, ushort&gt;</c> usage), proportionally scaled
    /// from the source bit depth (not left-shift replication). The returned array is exactly
    /// <c>outWidth * outHeight * channels * bytesPerChannel</c> bytes -- callers (including tests) rely on
    /// that exact size, so it is a plain heap allocation rather than a (possibly larger) pool rental; a
    /// caller that wants a pooled buffer for the final <see cref="Image"/> rents and copies separately.
    /// </summary>
    public static byte[] Convert(
        int[][] colorPlanes,
        int[] colorWidths,
        bool monoChrome,
        bool subsamplingX,
        bool subsamplingY,
        int matrixCoefficients,
        bool colorRangeFull,
        int bitDepth,
        int[]? alphaPlane,
        int alphaWidth,
        int outWidth,
        int outHeight)
    {
        bool hasAlpha = alphaPlane is not null;
        bool output16Bit = bitDepth > 8;
        int outMax = output16Bit ? 65535 : 255;
        int srcMax = (1 << bitDepth) - 1;
        int bytesPerChannel = output16Bit ? 2 : 1;
        int channels = monoChrome ? (hasAlpha ? 4 : 1) : hasAlpha ? 4 : 3;

        var pixels = new byte[outWidth * outHeight * channels * bytesPerChannel];

        void WriteChannel(int byteOffset, int value)
        {
            if (output16Bit)
            {
                ushort v16 = (ushort)value;
                pixels[byteOffset] = (byte)v16;
                pixels[byteOffset + 1] = (byte)(v16 >> 8);
            }
            else
            {
                pixels[byteOffset] = (byte)value;
            }
        }

        var yPlane = colorPlanes[0];
        int yStride = colorWidths[0];

        if (monoChrome)
        {
            for (int row = 0; row < outHeight; row++)
            {
                for (int col = 0; col < outWidth; col++)
                {
                    int gray = ScaleSample(yPlane[(row * yStride) + col], srcMax, outMax);
                    int idx = ((row * outWidth) + col) * channels * bytesPerChannel;
                    if (hasAlpha)
                    {
                        WriteChannel(idx, gray);
                        WriteChannel(idx + bytesPerChannel, gray);
                        WriteChannel(idx + (2 * bytesPerChannel), gray);
                        WriteChannel(idx + (3 * bytesPerChannel), ScaleSample(alphaPlane![(row * alphaWidth) + col], srcMax, outMax));
                    }
                    else
                    {
                        WriteChannel(idx, gray);
                    }
                }
            }

            return pixels;
        }

        var uPlane = colorPlanes[1];
        var vPlane = colorPlanes[2];
        int uStride = colorWidths[1];
        int vStride = colorWidths[2];
        int subX = subsamplingX ? 1 : 0;
        int subY = subsamplingY ? 1 : 0;

        bool identity = matrixCoefficients == 0;
        double kr = 0, kb = 0, kg = 0;
        double yLo = 0, yRange = 0, cLo = 0, cRange = 0;
        if (!identity)
        {
            (kr, kb) = GetCoefficients(matrixCoefficients);
            kg = 1.0 - kr - kb;

            int bitShift = bitDepth - 8;
            yLo = colorRangeFull ? 0 : 16 << bitShift;
            yRange = colorRangeFull ? srcMax : 219 << bitShift;
            cLo = colorRangeFull ? 1 << (bitDepth - 1) : 128 << bitShift;

            // Full-range chroma spans the *entire* sample range (0..srcMax, centered on cLo) the same way
            // full-range luma spans the entire 0..srcMax via yRange above -- confirmed empirically against
            // ffmpeg's libdav1d-backed AVIF decode (an independent AV1 decoder) after this line previously
            // read `srcMax / 2.0`: that halved-swing value made every full-range, non-gray corpus file decode
            // to visibly over-saturated/clipped colors (e.g. extended_pixi.avif's first pixel decoded to
            // (0,255,0) here vs. ffmpeg's (0,131,0) for the same Y=1/U=4/V=4 source samples -- consistent with
            // exactly a 2x-too-large chroma excursion, not a rounding-level discrepancy). `224 << bitShift`
            // below (the limited-range case) was already correct by the same reasoning: limited-range chroma's
            // valid excursion is the sub-range 16..240, a full swing of 224, not 112.
            cRange = colorRangeFull ? srcMax : 224 << bitShift;
        }

        var twoKr = Vector128.Create(2 * (1 - kr));
        var twoKb = Vector128.Create(2 * (1 - kb));
        var krVec = Vector128.Create(kr);
        var kbVec = Vector128.Create(kb);
        var kgVec = Vector128.Create(kg);
        var yLoVec = Vector128.Create(yLo);
        var yRangeVec = Vector128.Create(yRange);
        var cLoVec = Vector128.Create(cLo);
        var cRangeVec = Vector128.Create(cRange);

        var twoKr256 = Vector256.Create(2 * (1 - kr));
        var twoKb256 = Vector256.Create(2 * (1 - kb));
        var krVec256 = Vector256.Create(kr);
        var kbVec256 = Vector256.Create(kb);
        var kgVec256 = Vector256.Create(kg);
        var yLoVec256 = Vector256.Create(yLo);
        var yRangeVec256 = Vector256.Create(yRange);
        var cLoVec256 = Vector256.Create(cLo);
        var cRangeVec256 = Vector256.Create(cRange);

        for (int row = 0; row < outHeight; row++)
        {
            int uvRow = row >> subY;
            int rowBase = row * yStride;
            int alphaRowBase = row * alphaWidth;
            int uvRowBaseU = uvRow * uStride;
            int uvRowBaseV = uvRow * vStride;
            int col = 0;

            // 4-wide Vector256<double> fast path for the non-identity color-matrix case, falling back to
            // 2-wide Vector128<double> and then scalar for the remainder. AVX2 (this repo's baseline x64
            // target) double arithmetic is bit-identical to scalar double arithmetic on the same hardware
            // regardless of vector width, so widening from 2 to 4 lanes is a pure speedup with no new
            // precision divergence to verify -- same reasoning that already justified the 2-wide path.
            if (!identity && Vector256.IsHardwareAccelerated)
            {
                for (; col + 3 < outWidth; col += 4)
                {
                    var ySamples = Vector256.Create((double)yPlane[rowBase + col], yPlane[rowBase + col + 1], yPlane[rowBase + col + 2], yPlane[rowBase + col + 3]);
                    var uSamples = Vector256.Create((double)uPlane[uvRowBaseU + (col >> subX)], uPlane[uvRowBaseU + ((col + 1) >> subX)], uPlane[uvRowBaseU + ((col + 2) >> subX)], uPlane[uvRowBaseU + ((col + 3) >> subX)]);
                    var vSamples = Vector256.Create((double)vPlane[uvRowBaseV + (col >> subX)], vPlane[uvRowBaseV + ((col + 1) >> subX)], vPlane[uvRowBaseV + ((col + 2) >> subX)], vPlane[uvRowBaseV + ((col + 3) >> subX)]);

                    var yn = (ySamples - yLoVec256) / yRangeVec256;
                    var cbn = (uSamples - cLoVec256) / cRangeVec256;
                    var crn = (vSamples - cLoVec256) / cRangeVec256;

                    var r = yn + (twoKr256 * crn);
                    var b = yn + (twoKb256 * cbn);
                    var g = (yn - (krVec256 * r) - (kbVec256 * b)) / kgVec256;

                    int channelStride = channels * bytesPerChannel;
                    int idx0 = ((row * outWidth) + col) * channelStride;
                    WriteChannel(idx0, ClampToRange(r[0], outMax));
                    WriteChannel(idx0 + bytesPerChannel, ClampToRange(g[0], outMax));
                    WriteChannel(idx0 + (2 * bytesPerChannel), ClampToRange(b[0], outMax));
                    if (hasAlpha)
                    {
                        WriteChannel(idx0 + (3 * bytesPerChannel), ScaleSample(alphaPlane![alphaRowBase + col], srcMax, outMax));
                    }

                    int idx1 = idx0 + channelStride;
                    WriteChannel(idx1, ClampToRange(r[1], outMax));
                    WriteChannel(idx1 + bytesPerChannel, ClampToRange(g[1], outMax));
                    WriteChannel(idx1 + (2 * bytesPerChannel), ClampToRange(b[1], outMax));
                    if (hasAlpha)
                    {
                        WriteChannel(idx1 + (3 * bytesPerChannel), ScaleSample(alphaPlane![alphaRowBase + col + 1], srcMax, outMax));
                    }

                    int idx2 = idx1 + channelStride;
                    WriteChannel(idx2, ClampToRange(r[2], outMax));
                    WriteChannel(idx2 + bytesPerChannel, ClampToRange(g[2], outMax));
                    WriteChannel(idx2 + (2 * bytesPerChannel), ClampToRange(b[2], outMax));
                    if (hasAlpha)
                    {
                        WriteChannel(idx2 + (3 * bytesPerChannel), ScaleSample(alphaPlane![alphaRowBase + col + 2], srcMax, outMax));
                    }

                    int idx3 = idx2 + channelStride;
                    WriteChannel(idx3, ClampToRange(r[3], outMax));
                    WriteChannel(idx3 + bytesPerChannel, ClampToRange(g[3], outMax));
                    WriteChannel(idx3 + (2 * bytesPerChannel), ClampToRange(b[3], outMax));
                    if (hasAlpha)
                    {
                        WriteChannel(idx3 + (3 * bytesPerChannel), ScaleSample(alphaPlane![alphaRowBase + col + 3], srcMax, outMax));
                    }
                }
            }

            // 2-wide Vector128<double> fast path for the non-identity color-matrix case: SSE2 (always
            // available on x64) double arithmetic is bit-identical to scalar double arithmetic on the same
            // hardware, so this is a pure speedup with no precision divergence from the scalar path below
            // -- the two never need separate correctness verification or separate baseline hashes.
            if (!identity)
            {
                for (; col + 1 < outWidth; col += 2)
                {
                    var ySamples = Vector128.Create((double)yPlane[rowBase + col], yPlane[rowBase + col + 1]);
                    var uSamples = Vector128.Create((double)uPlane[uvRowBaseU + (col >> subX)], uPlane[uvRowBaseU + ((col + 1) >> subX)]);
                    var vSamples = Vector128.Create((double)vPlane[uvRowBaseV + (col >> subX)], vPlane[uvRowBaseV + ((col + 1) >> subX)]);

                    var yn = (ySamples - yLoVec) / yRangeVec;
                    var cbn = (uSamples - cLoVec) / cRangeVec;
                    var crn = (vSamples - cLoVec) / cRangeVec;

                    var r = yn + (twoKr * crn);
                    var b = yn + (twoKb * cbn);
                    var g = (yn - (krVec * r) - (kbVec * b)) / kgVec;

                    int idx0 = ((row * outWidth) + col) * channels * bytesPerChannel;
                    WriteChannel(idx0, ClampToRange(r[0], outMax));
                    WriteChannel(idx0 + bytesPerChannel, ClampToRange(g[0], outMax));
                    WriteChannel(idx0 + (2 * bytesPerChannel), ClampToRange(b[0], outMax));
                    if (hasAlpha)
                    {
                        WriteChannel(idx0 + (3 * bytesPerChannel), ScaleSample(alphaPlane![alphaRowBase + col], srcMax, outMax));
                    }

                    int idx1 = idx0 + (channels * bytesPerChannel);
                    WriteChannel(idx1, ClampToRange(r[1], outMax));
                    WriteChannel(idx1 + bytesPerChannel, ClampToRange(g[1], outMax));
                    WriteChannel(idx1 + (2 * bytesPerChannel), ClampToRange(b[1], outMax));
                    if (hasAlpha)
                    {
                        WriteChannel(idx1 + (3 * bytesPerChannel), ScaleSample(alphaPlane![alphaRowBase + col + 1], srcMax, outMax));
                    }
                }
            }

            for (; col < outWidth; col++)
            {
                int uvCol = col >> subX;
                int ySample = yPlane[rowBase + col];
                int uSample = uPlane[uvRowBaseU + uvCol];
                int vSample = vPlane[uvRowBaseV + uvCol];

                int rOut, gOut, bOut;
                if (identity)
                {
                    // Identity matrix: Y/Cb/Cr slots carry G/B/R directly, no cross-channel mixing.
                    gOut = ScaleSample(ySample, srcMax, outMax);
                    bOut = ScaleSample(uSample, srcMax, outMax);
                    rOut = ScaleSample(vSample, srcMax, outMax);
                }
                else
                {
                    double yn = (ySample - yLo) / yRange;
                    double cbn = (uSample - cLo) / cRange;
                    double crn = (vSample - cLo) / cRange;

                    double r = yn + (2 * (1 - kr) * crn);
                    double b = yn + (2 * (1 - kb) * cbn);
                    double g = (yn - (kr * r) - (kb * b)) / kg;

                    rOut = ClampToRange(r, outMax);
                    gOut = ClampToRange(g, outMax);
                    bOut = ClampToRange(b, outMax);
                }

                int idx = ((row * outWidth) + col) * channels * bytesPerChannel;
                WriteChannel(idx, rOut);
                WriteChannel(idx + bytesPerChannel, gOut);
                WriteChannel(idx + (2 * bytesPerChannel), bOut);
                if (hasAlpha)
                {
                    WriteChannel(idx + (3 * bytesPerChannel), ScaleSample(alphaPlane![alphaRowBase + col], srcMax, outMax));
                }
            }
        }

        return pixels;
    }

    private static int ScaleSample(int sample, int srcMax, int outMax) =>
        Math.Clamp((int)Math.Round((double)sample * outMax / srcMax), 0, outMax);

    private static int ClampToRange(double normalized, int outMax) =>
        Math.Clamp((int)Math.Round(normalized * outMax), 0, outMax);

    /// <summary><c>matrix_coefficients</c> (spec §6.4.2, ITU-T H.273 CICP) luma coefficients. Unrecognized/unspecified values fall back to BT.601, matching common decoder practice.</summary>
    private static (double Kr, double Kb) GetCoefficients(int matrixCoefficients) => matrixCoefficients switch
    {
        1 => (0.2126, 0.0722), // BT.709
        9 => (0.2627, 0.0593), // BT.2020 non-constant luminance
        5 or 6 => (0.299, 0.114), // BT.601 / BT.470BG / SMPTE170M
        _ => (0.299, 0.114),
    };
}
