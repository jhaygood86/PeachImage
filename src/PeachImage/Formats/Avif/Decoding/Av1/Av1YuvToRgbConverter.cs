using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

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
    public static byte[] Convert<TColor, TAlpha>(
        TColor[][] colorPlanes,
        int[] colorWidths,
        bool monoChrome,
        bool subsamplingX,
        bool subsamplingY,
        int matrixCoefficients,
        bool colorRangeFull,
        int bitDepth,
        TAlpha[]? alphaPlane,
        int alphaWidth,
        int outWidth,
        int outHeight)
        where TColor : unmanaged, IBinaryInteger<TColor>
        where TAlpha : unmanaged, IBinaryInteger<TAlpha>
    {
        var pixels = new byte[OutputByteCount(monoChrome, bitDepth, alphaPlane is not null, outWidth, outHeight)];
        ConvertInto(pixels, colorPlanes, colorWidths, monoChrome, subsamplingX, subsamplingY, matrixCoefficients, colorRangeFull, bitDepth, alphaPlane, alphaWidth, outWidth, outHeight);
        return pixels;
    }

    /// <summary>
    /// <see cref="ConvertInto{TColor, TAlpha}"/> for composited frames of any sample type: picks the
    /// instantiation matching the color and (optional) alpha planes' storage types.
    /// </summary>
    public static void ConvertInto(
        byte[] pixels,
        Av1TileComposer.CompositedFrame color,
        Av1TileComposer.CompositedFrame? alpha,
        int outWidth,
        int outHeight)
    {
        switch (color)
        {
            case Av1TileComposer.CompositedFrame<byte> c:
                ConvertWithAlpha(pixels, c, alpha, outWidth, outHeight);
                break;
            default:
                ConvertWithAlpha(pixels, (Av1TileComposer.CompositedFrame<ushort>)color, alpha, outWidth, outHeight);
                break;
        }
    }

    private static void ConvertWithAlpha<TColor>(byte[] pixels, Av1TileComposer.CompositedFrame<TColor> color, Av1TileComposer.CompositedFrame? alpha, int outWidth, int outHeight)
        where TColor : unmanaged, IBinaryInteger<TColor>
    {
        var seq = color.Sequence;
        switch (alpha)
        {
            case null:
                ConvertInto<TColor, byte>(pixels, color.Planes, color.Widths, seq.MonoChrome, seq.SubsamplingX, seq.SubsamplingY, seq.MatrixCoefficients, seq.ColorRange, seq.BitDepth, null, 0, outWidth, outHeight);
                break;
            case Av1TileComposer.CompositedFrame<byte> a:
                ConvertInto(pixels, color.Planes, color.Widths, seq.MonoChrome, seq.SubsamplingX, seq.SubsamplingY, seq.MatrixCoefficients, seq.ColorRange, seq.BitDepth, a.Planes[0], a.Widths[0], outWidth, outHeight);
                break;
            default:
                var wide = (Av1TileComposer.CompositedFrame<ushort>)alpha;
                ConvertInto(pixels, color.Planes, color.Widths, seq.MonoChrome, seq.SubsamplingX, seq.SubsamplingY, seq.MatrixCoefficients, seq.ColorRange, seq.BitDepth, wide.Planes[0], wide.Widths[0], outWidth, outHeight);
                break;
        }
    }

    /// <summary>The exact number of bytes <see cref="ConvertInto"/> writes (and <see cref="Convert"/> returns) for the given format.</summary>
    public static int OutputByteCount(bool monoChrome, int bitDepth, bool hasAlpha, int outWidth, int outHeight)
    {
        int bytesPerChannel = bitDepth > 8 ? 2 : 1;
        int channels = monoChrome ? (hasAlpha ? 4 : 1) : hasAlpha ? 4 : 3;
        return outWidth * outHeight * channels * bytesPerChannel;
    }

    /// <summary>
    /// <see cref="Convert"/> into a caller-supplied buffer of at least <see cref="OutputByteCount"/> bytes
    /// (typically pool-rented, so its prior contents are arbitrary): every one of those bytes is written.
    /// </summary>
    public static void ConvertInto<TColor, TAlpha>(
        byte[] pixels,
        TColor[][] colorPlanes,
        int[] colorWidths,
        bool monoChrome,
        bool subsamplingX,
        bool subsamplingY,
        int matrixCoefficients,
        bool colorRangeFull,
        int bitDepth,
        TAlpha[]? alphaPlane,
        int alphaWidth,
        int outWidth,
        int outHeight)
        where TColor : unmanaged, IBinaryInteger<TColor>
        where TAlpha : unmanaged, IBinaryInteger<TAlpha>
    {
        // Rows are independent (each reads only its own source rows and writes only its own output bytes),
        // so tall images are converted in row bands on the thread pool.
        int bands = Math.Min(Environment.ProcessorCount, outHeight / MinRowsPerBand);
        if (bands < 2)
        {
            ConvertRows(pixels, colorPlanes, colorWidths, monoChrome, subsamplingX, subsamplingY, matrixCoefficients, colorRangeFull, bitDepth, alphaPlane, alphaWidth, outWidth, 0, outHeight);
            return;
        }

        // Band edges are even rows so a 4:2:0 chroma row is never shared in a way that matters (reads only).
        int bandRows = ((outHeight + bands - 1) / bands + 1) & ~1;
        int bandCount = (outHeight + bandRows - 1) / bandRows;
        Parallel.For(0, bandCount, band =>
        {
            int start = band * bandRows;
            int end = Math.Min(outHeight, start + bandRows);
            ConvertRows(pixels, colorPlanes, colorWidths, monoChrome, subsamplingX, subsamplingY, matrixCoefficients, colorRangeFull, bitDepth, alphaPlane, alphaWidth, outWidth, start, end);
        });
    }

    /// <summary>Images shorter than twice this are converted on the calling thread.</summary>
    private const int MinRowsPerBand = 128;

    [SkipLocalsInit]
    private static void ConvertRows<TColor, TAlpha>(
        byte[] pixels,
        TColor[][] colorPlanes,
        int[] colorWidths,
        bool monoChrome,
        bool subsamplingX,
        bool subsamplingY,
        int matrixCoefficients,
        bool colorRangeFull,
        int bitDepth,
        TAlpha[]? alphaPlane,
        int alphaWidth,
        int outWidth,
        int rowStart,
        int rowEnd)
        where TColor : unmanaged, IBinaryInteger<TColor>
        where TAlpha : unmanaged, IBinaryInteger<TAlpha>
    {
        bool hasAlpha = alphaPlane is not null;
        bool output16Bit = bitDepth > 8;
        int outMax = output16Bit ? 65535 : 255;
        int srcMax = (1 << bitDepth) - 1;
        int bytesPerChannel = output16Bit ? 2 : 1;
        int channels = monoChrome ? (hasAlpha ? 4 : 1) : hasAlpha ? 4 : 3;

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
            for (int row = rowStart; row < rowEnd; row++)
            {
                for (int col = 0; col < outWidth; col++)
                {
                    int gray = ScaleSample(int.CreateTruncating(yPlane[(row * yStride) + col]), srcMax, outMax);
                    int idx = ((row * outWidth) + col) * channels * bytesPerChannel;
                    if (hasAlpha)
                    {
                        WriteChannel(idx, gray);
                        WriteChannel(idx + bytesPerChannel, gray);
                        WriteChannel(idx + (2 * bytesPerChannel), gray);
                        WriteChannel(idx + (3 * bytesPerChannel), ScaleSample(int.CreateTruncating(alphaPlane![(row * alphaWidth) + col]), srcMax, outMax));
                    }
                    else
                    {
                        WriteChannel(idx, gray);
                    }
                }
            }

            return;
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
        var outMaxVec256 = Vector256.Create((double)outMax);

        // The per-sample normalisations (sample - lo) / range depend only on the sample value, so they are
        // tabulated once per call: the table entries are the very same double divisions the per-pixel code
        // would perform, so results are bit-identical, minus three vector divisions per four pixels.
        // Sized for the largest sample any supported tile bit depth (<= 10) can hold rather than srcMax,
        // since the planes of a grid's tiles are not required to share the primary item's bit depth.
        const int TableSize = 1024;
        Span<double> ynTab = stackalloc double[TableSize];
        Span<double> cbnTab = stackalloc double[TableSize];
        if (!identity)
        {
            for (int v = 0; v < TableSize; v++)
            {
                ynTab[v] = (v - yLo) / yRange;
                cbnTab[v] = (v - cLo) / cRange;
            }
        }

        Span<int> alphaTab = hasAlpha ? stackalloc int[TableSize] : default;
        for (int v = 0; v < alphaTab.Length; v++)
        {
            alphaTab[v] = ScaleSample(v, srcMax, outMax);
        }

        Span<int> rLane = stackalloc int[4];
        Span<int> gLane = stackalloc int[4];
        Span<int> bLane = stackalloc int[4];

        for (int row = rowStart; row < rowEnd; row++)
        {
            int uvRow = row >> subY;
            int rowBase = row * yStride;
            int alphaRowBase = row * alphaWidth;
            int uvRowBaseU = uvRow * uStride;
            int uvRowBaseV = uvRow * vStride;
            int col = 0;

            // 4-wide Vector256<double> fast path for the non-identity color-matrix case, falling back to
            // 2-wide Vector128<double> and then scalar for the remainder. Double arithmetic is bit-identical
            // to scalar double arithmetic on the same hardware regardless of vector width, and the rounding
            // below (VROUNDPD, nearest-even) is exactly Math.Round's default, so this matches the scalar
            // path below sample for sample.
            if (!identity && Avx.IsSupported)
            {
                int channelStride = channels * bytesPerChannel;
                for (; col + 3 < outWidth; col += 4)
                {
                    int uc = uvRowBaseU + (col >> subX);
                    int vc = uvRowBaseV + (col >> subX);
                    int u1 = uvRowBaseU + ((col + 1) >> subX);
                    int u2 = uvRowBaseU + ((col + 2) >> subX);
                    int u3 = uvRowBaseU + ((col + 3) >> subX);
                    int v1 = uvRowBaseV + ((col + 1) >> subX);
                    int v2 = uvRowBaseV + ((col + 2) >> subX);
                    int v3 = uvRowBaseV + ((col + 3) >> subX);

                    var yn = Vector256.Create(
                        Normalized(ynTab, int.CreateTruncating(yPlane[rowBase + col]), yLo, yRange),
                        Normalized(ynTab, int.CreateTruncating(yPlane[rowBase + col + 1]), yLo, yRange),
                        Normalized(ynTab, int.CreateTruncating(yPlane[rowBase + col + 2]), yLo, yRange),
                        Normalized(ynTab, int.CreateTruncating(yPlane[rowBase + col + 3]), yLo, yRange));
                    var cbn = Vector256.Create(
                        Normalized(cbnTab, int.CreateTruncating(uPlane[uc]), cLo, cRange),
                        Normalized(cbnTab, int.CreateTruncating(uPlane[u1]), cLo, cRange),
                        Normalized(cbnTab, int.CreateTruncating(uPlane[u2]), cLo, cRange),
                        Normalized(cbnTab, int.CreateTruncating(uPlane[u3]), cLo, cRange));
                    var crn = Vector256.Create(
                        Normalized(cbnTab, int.CreateTruncating(vPlane[vc]), cLo, cRange),
                        Normalized(cbnTab, int.CreateTruncating(vPlane[v1]), cLo, cRange),
                        Normalized(cbnTab, int.CreateTruncating(vPlane[v2]), cLo, cRange),
                        Normalized(cbnTab, int.CreateTruncating(vPlane[v3]), cLo, cRange));

                    var r = yn + (twoKr256 * crn);
                    var b = yn + (twoKb256 * cbn);
                    var g = (yn - (krVec256 * r) - (kbVec256 * b)) / kgVec256;

                    ToClampedInts(r, outMaxVec256).StoreUnsafe(ref MemoryMarshal.GetReference(rLane));
                    ToClampedInts(g, outMaxVec256).StoreUnsafe(ref MemoryMarshal.GetReference(gLane));
                    ToClampedInts(b, outMaxVec256).StoreUnsafe(ref MemoryMarshal.GetReference(bLane));

                    int idx = ((row * outWidth) + col) * channelStride;
                    for (int k = 0; k < 4; k++)
                    {
                        if (output16Bit)
                        {
                            pixels[idx] = (byte)rLane[k];
                            pixels[idx + 1] = (byte)(rLane[k] >> 8);
                            pixels[idx + 2] = (byte)gLane[k];
                            pixels[idx + 3] = (byte)(gLane[k] >> 8);
                            pixels[idx + 4] = (byte)bLane[k];
                            pixels[idx + 5] = (byte)(bLane[k] >> 8);
                            if (hasAlpha)
                            {
                                int a16 = AlphaOut(alphaTab, int.CreateTruncating(alphaPlane![alphaRowBase + col + k]), srcMax, outMax);
                                pixels[idx + 6] = (byte)a16;
                                pixels[idx + 7] = (byte)(a16 >> 8);
                            }
                        }
                        else
                        {
                            pixels[idx] = (byte)rLane[k];
                            pixels[idx + 1] = (byte)gLane[k];
                            pixels[idx + 2] = (byte)bLane[k];
                            if (hasAlpha)
                            {
                                pixels[idx + 3] = (byte)AlphaOut(alphaTab, int.CreateTruncating(alphaPlane![alphaRowBase + col + k]), srcMax, outMax);
                            }
                        }

                        idx += channelStride;
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
                    var ySamples = Vector128.Create((double)int.CreateTruncating(yPlane[rowBase + col]), int.CreateTruncating(yPlane[rowBase + col + 1]));
                    var uSamples = Vector128.Create((double)int.CreateTruncating(uPlane[uvRowBaseU + (col >> subX)]), int.CreateTruncating(uPlane[uvRowBaseU + ((col + 1) >> subX)]));
                    var vSamples = Vector128.Create((double)int.CreateTruncating(vPlane[uvRowBaseV + (col >> subX)]), int.CreateTruncating(vPlane[uvRowBaseV + ((col + 1) >> subX)]));

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
                        WriteChannel(idx0 + (3 * bytesPerChannel), ScaleSample(int.CreateTruncating(alphaPlane![alphaRowBase + col]), srcMax, outMax));
                    }

                    int idx1 = idx0 + (channels * bytesPerChannel);
                    WriteChannel(idx1, ClampToRange(r[1], outMax));
                    WriteChannel(idx1 + bytesPerChannel, ClampToRange(g[1], outMax));
                    WriteChannel(idx1 + (2 * bytesPerChannel), ClampToRange(b[1], outMax));
                    if (hasAlpha)
                    {
                        WriteChannel(idx1 + (3 * bytesPerChannel), ScaleSample(int.CreateTruncating(alphaPlane![alphaRowBase + col + 1]), srcMax, outMax));
                    }
                }
            }

            for (; col < outWidth; col++)
            {
                int uvCol = col >> subX;
                int ySample = int.CreateTruncating(yPlane[rowBase + col]);
                int uSample = int.CreateTruncating(uPlane[uvRowBaseU + uvCol]);
                int vSample = int.CreateTruncating(vPlane[uvRowBaseV + uvCol]);

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
                    WriteChannel(idx + (3 * bytesPerChannel), ScaleSample(int.CreateTruncating(alphaPlane![alphaRowBase + col]), srcMax, outMax));
                }
            }
        }

    }

    /// <summary><c>Clamp((int)Math.Round(x * outMax), 0, outMax)</c> for four lanes; <paramref name="scaled"/> is <c>x</c> and <paramref name="outMax"/> the broadcast maximum. AVX only.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ToClampedInts(Vector256<double> scaled, Vector256<double> outMax)
    {
        Vector256<double> rounded = Avx.RoundToNearestInteger(scaled * outMax);
        return Avx.ConvertToVector128Int32(Vector256.Min(Vector256.Max(rounded, Vector256<double>.Zero), outMax));
    }

    /// <summary>Tabulated <see cref="ScaleSample"/>; falls back to the direct computation for a sample beyond the table (an alpha plane may carry more bits than the color planes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlphaOut(Span<int> table, int sample, int srcMax, int outMax) =>
        (uint)sample < (uint)table.Length ? table[sample] : ScaleSample(sample, srcMax, outMax);

    /// <summary><c>(sample - lo) / range</c> through the table; samples outside it (never produced by a conforming decode, but possible from unclamped tile padding) take the direct computation, so results never differ from the scalar path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Normalized(Span<double> table, int sample, double lo, double range) =>
        (uint)sample < (uint)table.Length ? table[sample] : (sample - lo) / range;

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
