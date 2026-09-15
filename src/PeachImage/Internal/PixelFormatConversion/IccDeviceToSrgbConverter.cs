using System.Runtime.Intrinsics;
using PeachImage.Internal.Icc;

namespace PeachImage.Internal.PixelFormatConversion;

/// <summary>
/// ICC-aware device→RGBA32 conversion for any device color space PeachImage's ICC engine supports (Gray,
/// RGB, or CMYK — whatever the profile declares via <see cref="IccProfile.DataColorSpace"/> and
/// <see cref="IccProfile.ChannelCount"/>): uses the profile's real device→PCS transform instead of a naive,
/// non-colorimetric formula. For JPEG's own CMYK integration this applies uniformly regardless of whether the
/// <see cref="PixelFormat.Cmyk32"/> bytes originated from direct CMYK, Adobe-inverted CMYK (already normalized
/// by the time it reaches here), or YCCK (already converted to CMY+K) — all three funnel through the same
/// <c>Cmyk32</c> convention before conversion is attempted. Also the engine behind the public
/// <see cref="PeachImage.IccColorProfile.ConvertToSrgb"/> API.
/// </summary>
internal static class IccDeviceToSrgbConverter
{
    // Bounds stackalloc'd scratch to a small, fixed size regardless of image dimensions.
    private const int BatchSize = 64;

    /// <summary>
    /// Attempts to convert <paramref name="deviceValues"/> (<paramref name="pixelCount"/> pixels of whatever
    /// channel count the profile declares) to <paramref name="rgba"/> using <paramref name="iccProfileBytes"/>.
    /// Returns <see langword="false"/> (writing nothing) on any parse failure or unsupported transform/device
    /// color space — callers should fall back to a naive kernel in that case.
    /// </summary>
    internal static bool TryConvert(ReadOnlySpan<byte> deviceValues, Span<byte> rgba, int pixelCount, byte[] iccProfileBytes)
    {
        if (!TryParse(iccProfileBytes, out var profile, out var intent))
        {
            return false;
        }

        Convert(deviceValues, rgba, pixelCount, profile, profile.ChannelCount, intent, blackPointCompensation: false);
        return true;
    }

    /// <summary>
    /// Attempts to convert using an already-parsed <paramref name="profile"/>, for callers (the public ICC API)
    /// that parse once and convert many times rather than re-parsing per call.
    /// </summary>
    internal static void Convert(ReadOnlySpan<byte> deviceValues, Span<byte> rgba, int pixelCount, IccProfile profile, IccIntent intent, bool blackPointCompensation = false) =>
        Convert(deviceValues, rgba, pixelCount, profile, profile.ChannelCount, intent, blackPointCompensation);

    /// <summary>
    /// Parses <paramref name="iccProfileBytes"/> and resolves the intent to use when <paramref name="intent"/>
    /// is <see langword="null"/>. Returns <see langword="false"/> on any parse failure or unsupported profile
    /// — matches this ICC engine's own graceful-fallback philosophy (mirroring Wacton/Unicolour's own
    /// <c>IccConfiguration</c>, see THIRD-PARTY-LICENSES.md): any parse or support failure just means "no
    /// usable ICC profile", not a decode-breaking error.
    /// </summary>
    internal static bool TryParse(byte[] iccProfileBytes, out IccProfile profile, out IccIntent intent)
    {
        try
        {
            profile = new IccProfile(iccProfileBytes);
            profile.ErrorIfUnsupported();
        }
        catch (Exception)
        {
            profile = null!;
            intent = default;
            return false;
        }

        intent = profile.DefaultIntent == IccIntent.Unspecified ? IccIntent.Perceptual : profile.DefaultIntent;
        return true;
    }

    // sRGB's own black point, in D50 PCS XYZ: display luminance 0 -> linear sRGB (0,0,0) -> XYZ (0,0,0),
    // exactly, since every stage from there to D50 PCS is a linear matrix multiply (0 maps to 0 regardless of
    // which fixed matrix is used). No profile object is needed to know this, unlike an arbitrary destination.
    private static readonly IccVector3 SrgbBlackXyzD50 = new(0, 0, 0);

    private static void Convert(ReadOnlySpan<byte> deviceValues, Span<byte> rgba, int pixelCount, IccProfile profile, int channelCount, IccIntent intent, bool blackPointCompensation)
    {
        Span<double> xs = stackalloc double[BatchSize];
        Span<double> ys = stackalloc double[BatchSize];
        Span<double> zs = stackalloc double[BatchSize];
        Span<double> normalizedDevice = stackalloc double[channelCount];

        bool applyBpc = blackPointCompensation && IccBlackPointCompensation.AppliesTo(intent);
        var sourceBlack = applyBpc ? profile.GetBlackPointXyzD50(intent) : default;

        int processed = 0;
        while (processed < pixelCount)
        {
            int batchCount = Math.Min(BatchSize, pixelCount - processed);

            // Device -> PCS is inherently per-pixel and data-dependent (each pixel indexes different CLUT/curve
            // table entries), so this stage is scalar -- it can't be vectorized the way a fixed matrix can.
            for (int i = 0; i < batchCount; i++)
            {
                int deviceOffset = (processed + i) * channelCount;
                for (int c = 0; c < channelCount; c++)
                {
                    normalizedDevice[c] = deviceValues[deviceOffset + c] / 255.0;
                }

                var xyz = profile.ToXyzD50(normalizedDevice, intent);
                if (applyBpc)
                {
                    xyz = IccBlackPointCompensation.Apply(xyz, sourceBlack, SrgbBlackXyzD50);
                }

                xs[i] = xyz.X;
                ys[i] = xyz.Y;
                zs[i] = xyz.Z;
            }

            var rgbaBatch = rgba.Slice(processed * 4, batchCount * 4);
            XyzBatchToRgba(xs[..batchCount], ys[..batchCount], zs[..batchCount], rgbaBatch);

            processed += batchCount;
        }
    }

    /// <summary>
    /// Converts a batch of D50 PCS XYZ triples (structure-of-arrays: one X/Y/Z span per channel, not
    /// interleaved) to RGBA32 bytes. The Bradford-adaptation-then-XYZ-to-sRGB matrix stage is genuinely
    /// vectorized (<see cref="IccMatrix3x3.MultiplyBatch(Vector256{double}, Vector256{double}, Vector256{double})"/>);
    /// the sRGB gamma companding step stays per-lane scalar (<see cref="IccColorMath.LinearToSrgbByte"/>) since
    /// it's a transcendental function with no safe, portable vectorized primitive in this runtime.
    /// </summary>
    internal static void XyzBatchToRgba(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, ReadOnlySpan<double> zs, Span<byte> rgba)
    {
        var matrix = IccColorMath.XyzD50ToLinearSrgb;
        int i = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 4 <= xs.Length; i += 4)
            {
                var xv = Vector256.Create(xs[i], xs[i + 1], xs[i + 2], xs[i + 3]);
                var yv = Vector256.Create(ys[i], ys[i + 1], ys[i + 2], ys[i + 3]);
                var zv = Vector256.Create(zs[i], zs[i + 1], zs[i + 2], zs[i + 3]);
                var (rv, gv, bv) = matrix.MultiplyBatch(xv, yv, zv);
                WriteLanes(rgba, i, rv, gv, bv, laneCount: 4);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 2 <= xs.Length; i += 2)
            {
                var xv = Vector128.Create(xs[i], xs[i + 1]);
                var yv = Vector128.Create(ys[i], ys[i + 1]);
                var zv = Vector128.Create(zs[i], zs[i + 1]);
                var (rv, gv, bv) = matrix.MultiplyBatch(xv, yv, zv);
                WriteLanes(rgba, i, rv, gv, bv, laneCount: 2);
            }
        }

        for (; i < xs.Length; i++)
        {
            var linear = matrix.Multiply(new IccVector3(xs[i], ys[i], zs[i]));
            WritePixel(rgba, i, linear.X, linear.Y, linear.Z);
        }
    }

    private static void WriteLanes(Span<byte> rgba, int startIndex, Vector256<double> r, Vector256<double> g, Vector256<double> b, int laneCount)
    {
        for (int lane = 0; lane < laneCount; lane++)
        {
            WritePixel(rgba, startIndex + lane, r[lane], g[lane], b[lane]);
        }
    }

    private static void WriteLanes(Span<byte> rgba, int startIndex, Vector128<double> r, Vector128<double> g, Vector128<double> b, int laneCount)
    {
        for (int lane = 0; lane < laneCount; lane++)
        {
            WritePixel(rgba, startIndex + lane, r[lane], g[lane], b[lane]);
        }
    }

    private static void WritePixel(Span<byte> rgba, int pixelIndex, double linearR, double linearG, double linearB)
    {
        int offset = pixelIndex * 4;
        rgba[offset] = IccColorMath.LinearToSrgbByte(linearR);
        rgba[offset + 1] = IccColorMath.LinearToSrgbByte(linearG);
        rgba[offset + 2] = IccColorMath.LinearToSrgbByte(linearB);
        rgba[offset + 3] = 255;
    }
}
