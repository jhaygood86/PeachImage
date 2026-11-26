using System.Buffers;
using PeachImage.Internal.Icc;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Internal.Color;

/// <summary>
/// Converts rows of straight, interleaved float RGBA from one RGB color space to another: decode the source transfer function, multiply by
/// the 3x3 primaries matrix (through D50 XYZ), encode the destination transfer function. Spaces whose transfer function is a standard one
/// (sRGB, linear, BT.709, a pure power, PQ, HLG) run on the vector kernels in <see cref="ColorKernels"/>; an ICC profile with an arbitrary
/// tone-curve table runs through 4096-entry lookup tables with linear interpolation (scalar); a LUT-based ICC source goes pixel by pixel
/// through the ICC engine to sRGB first. Alpha is never touched.
/// </summary>
internal sealed class ColorTransform
{
    private readonly Space source;
    private readonly Space destination;
    private readonly float[] matrix;
    private readonly bool identityMatrix;
    private readonly bool bypass;
    private readonly bool clip;
    private readonly IccColorProfile? lutSource;
    private readonly IccRenderingIntent? intent;
    private readonly bool sourceHlg;
    private readonly bool destinationHlg;
    private readonly bool toneMap;
    private readonly float whitePoint;

    private ColorTransform(Space source, Space destination, bool clip, IccColorProfile? lutSource, IccRenderingIntent? intent, bool toneMap, double peakNits)
    {
        this.source = lutSource is null ? source : Space.Of(NamedColorSpace.Srgb);
        this.destination = destination;
        this.clip = clip;
        this.lutSource = lutSource;
        this.intent = intent;
        sourceHlg = this.source.Transfer == TransferFunction.Hlg;
        destinationHlg = destination.Transfer == TransferFunction.Hlg;
        this.toneMap = toneMap && this.source.Transfer is TransferFunction.Pq or TransferFunction.Hlg;
        whitePoint = (float)Math.Max(peakNits / Hdr.ReferenceWhiteNits, 1.0);

        var m = destination.FromXyz.Multiply(this.source.ToXyz).ToArray();
        matrix = new float[9];
        for (int i = 0; i < 9; i++)
        {
            matrix[i] = (float)m[i];
        }

        identityMatrix = IsIdentity(m);
        bypass = lutSource is null && identityMatrix && !this.toneMap && this.source.SameEncoding(destination);
    }

    public static ColorTransform Create(IccColorProfile? sourceProfile, NamedColorSpace sourceSpace, IccColorProfile? destinationProfile, NamedColorSpace destinationSpace, bool clip, IccRenderingIntent? intent, bool toneMap = false, double peakNits = 1000)
    {
        // A built-in profile read back from an image (or handed in) stands for its named space exactly, not the 16.16-quantized colorants it stores.
        if (sourceProfile is not null && BuiltIn(sourceProfile) is { } sourceNamed)
        {
            sourceProfile = null;
            sourceSpace = sourceNamed;
        }

        if (destinationProfile is not null && BuiltIn(destinationProfile) is { } destinationNamed)
        {
            destinationProfile = null;
            destinationSpace = destinationNamed;
        }

        IccColorProfile? lut = null;
        Space src;
        if (sourceProfile is null)
        {
            src = Space.Of(sourceSpace);
        }
        else if (!Space.TryOf(sourceProfile, out src))
        {
            if (sourceProfile.DataColorSpace != IccColorSpace.Rgb)
            {
                throw new IccProfileException($"A {sourceProfile.DataColorSpace} ICC profile cannot be the source of an RGB color space conversion.");
            }

            // A LUT-based RGB profile: converted to sRGB by the ICC engine first, then onward as sRGB.
            lut = sourceProfile;
            src = Space.Of(NamedColorSpace.Srgb);
        }

        Space dst;
        if (destinationProfile is null)
        {
            dst = Space.Of(destinationSpace);
        }
        else if (!Space.TryOf(destinationProfile, out dst))
        {
            throw new IccProfileException(
                "The destination ICC profile is LUT-based (or not an RGB matrix/TRC profile), which PeachImage can read from but not convert into. " +
                "Use a matrix/TRC profile, or one of the built-in NamedColorSpace values, as the destination.");
        }

        return new ColorTransform(src, dst, clip, lut, intent, toneMap, peakNits);
    }

    private static NamedColorSpace? BuiltIn(IccColorProfile profile)
    {
        var bytes = profile.ProfileBytes.Span;
        foreach (var space in Enum.GetValues<NamedColorSpace>())
        {
            if (ColorProfiles.HasProfile(space) && bytes.SequenceEqual(ColorProfiles.GetBytes(space).Span))
            {
                return space;
            }
        }

        return null;
    }

    /// <summary>Converts the color channels of one row of <paramref name="width"/> interleaved RGBA pixels, in place.</summary>
    public void Apply(float[] rgba, int width) => ApplyInterleaved(rgba.AsSpan(0, width * 4), width);

    /// <summary>
    /// Converts the color of <paramref name="width"/> interleaved RGBA pixels in place without transposing to planes: transfer functions, clamp and the
    /// matrix run on the interleaved row (see <see cref="InterleavedColorKernels"/>). Only the HDR stages that need a pixel's luminance (the HLG system gamma
    /// and tone mapping) go through planes, and only for HDR sources and destinations.
    /// </summary>
    public void ApplyInterleaved(Span<float> rgba, int width)
    {
        if (lutSource is not null)
        {
            ConvertLutToSrgbInterleaved(rgba, width);
        }

        if (bypass)
        {
            if (clip)
            {
                InterleavedColorKernels.ClampUnit(rgba);
            }

            return;
        }

        source.ToLinearInterleaved(rgba);
        if (sourceHlg || toneMap)
        {
            LuminanceStages(rgba, width, beforeMatrix: true);
        }

        if (!identityMatrix)
        {
            InterleavedColorKernels.Matrix3x3(rgba, width, matrix);
        }

        if (destinationHlg)
        {
            LuminanceStages(rgba, width, beforeMatrix: false);
        }

        destination.FromLinearInterleaved(rgba);
        if (clip)
        {
            InterleavedColorKernels.ClampUnit(rgba);
        }
    }

    private void LuminanceStages(Span<float> rgba, int width, bool beforeMatrix)
    {
        var pool = ArrayPool<float>.Shared;
        float[] scratch = pool.Rent(width * 4);
        try
        {
            var r = scratch.AsSpan(0, width);
            var g = scratch.AsSpan(width, width);
            var b = scratch.AsSpan(2 * width, width);
            var a = scratch.AsSpan(3 * width, width);
            RgbaRowKernels.Deinterleave(rgba, r, g, b, a, width);
            if (beforeMatrix)
            {
                if (sourceHlg)
                {
                    ColorKernels.HlgOotf(r, g, b);
                }

                if (toneMap)
                {
                    ColorKernels.ToneMapReinhard(r, g, b, whitePoint);
                }
            }
            else
            {
                ColorKernels.HlgInverseOotf(r, g, b);
            }

            RgbaRowKernels.Interleave(r, g, b, a, rgba, width);
        }
        finally
        {
            pool.Return(scratch);
        }
    }

    private void ConvertLutToSrgbInterleaved(Span<float> rgba, int width)
    {
        Span<float> device = stackalloc float[3];
        for (int i = 0; i < width; i++)
        {
            int o = i * 4;
            device[0] = rgba[o];
            device[1] = rgba[o + 1];
            device[2] = rgba[o + 2];
            if (lutSource!.TryConvertToSrgb(device, out float sr, out float sg, out float sb, intent))
            {
                rgba[o] = sr;
                rgba[o + 1] = sg;
                rgba[o + 2] = sb;
            }
        }
    }

    /// <summary>Converts three planes of color samples in place.</summary>
    public void ApplyPlanes(Span<float> r, Span<float> g, Span<float> b)
    {
        if (lutSource is not null)
        {
            ConvertLutToSrgb(r, g, b);
        }

        if (bypass)
        {
            if (clip)
            {
                ColorKernels.ClampUnit(r);
                ColorKernels.ClampUnit(g);
                ColorKernels.ClampUnit(b);
            }

            return;
        }

        source.ToLinear(r, g, b);
        if (sourceHlg)
        {
            ColorKernels.HlgOotf(r, g, b);
        }

        if (toneMap)
        {
            ColorKernels.ToneMapReinhard(r, g, b, whitePoint);
        }

        if (!identityMatrix)
        {
            ColorKernels.Matrix3x3(r, g, b, matrix);
        }

        if (destinationHlg)
        {
            ColorKernels.HlgInverseOotf(r, g, b);
        }

        destination.FromLinear(r, g, b);
        if (clip)
        {
            ColorKernels.ClampUnit(r);
            ColorKernels.ClampUnit(g);
            ColorKernels.ClampUnit(b);
        }
    }

    private void ConvertLutToSrgb(Span<float> r, Span<float> g, Span<float> b)
    {
        Span<float> device = stackalloc float[3];
        for (int i = 0; i < r.Length; i++)
        {
            device[0] = r[i];
            device[1] = g[i];
            device[2] = b[i];
            if (lutSource!.TryConvertToSrgb(device, out float sr, out float sg, out float sb, intent))
            {
                r[i] = sr;
                g[i] = sg;
                b[i] = sb;
            }
        }
    }

    private static bool IsIdentity(double[] m)
    {
        for (int i = 0; i < 9; i++)
        {
            double expected = i % 4 == 0 ? 1.0 : 0.0;
            if (Math.Abs(m[i] - expected) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One side of the conversion: its primaries as a matrix to D50 XYZ, and how to get to and from linear light.</summary>
    private sealed class Space
    {
        private const int TableSize = 4096;

        private TransferFunction transfer;
        private double gamma;
        private float[]?[]? decodeTables;
        private float[]?[]? encodeTables;

        public TransferFunction Transfer => transfer;

        public bool SameEncoding(Space other) => transfer == other.transfer && gamma == other.gamma && decodeTables is null && other.decodeTables is null;

        public IccMatrix3x3 ToXyz { get; private set; }

        public IccMatrix3x3 FromXyz { get; private set; }

        public static Space Of(NamedColorSpace named) => new()
        {
            transfer = NamedSpaces.Transfer(named),
            ToXyz = NamedSpaces.ToXyzD50(named),
            FromXyz = NamedSpaces.FromXyzD50(named),
        };

        public static bool TryOf(IccColorProfile profile, out Space space)
        {
            space = null!;
            var engine = profile.Engine;
            if (profile.DataColorSpace != IccColorSpace.Rgb || !engine.TryGetRgbMatrixTrc(out var toXyz, out var curves))
            {
                return false;
            }

            space = new Space { ToXyz = toXyz, FromXyz = toXyz.Inverse() };
            var description = profile.Describe();
            if (description is { Transfer: not TransferFunction.Unknown } known)
            {
                space.transfer = known.Transfer;
                space.gamma = known.Gamma;
                return true;
            }

            space.transfer = TransferFunction.Unknown;
            space.decodeTables = new float[]?[3];
            space.encodeTables = new float[]?[3];
            for (int c = 0; c < 3; c++)
            {
                space.decodeTables[c] = BuildDecodeTable(curves[c]);
                space.encodeTables[c] = BuildEncodeTable(curves[c]);
            }

            return true;
        }

        public void ToLinearInterleaved(Span<float> rgba)
        {
            if (decodeTables is null)
            {
                InterleavedColorKernels.ToLinear(transfer, gamma, rgba);
                return;
            }

            LookupInterleaved(rgba, decodeTables);
        }

        public void FromLinearInterleaved(Span<float> rgba)
        {
            if (encodeTables is null)
            {
                InterleavedColorKernels.FromLinear(transfer, gamma, rgba);
                return;
            }

            LookupInterleaved(rgba, encodeTables);
        }

        private static void LookupInterleaved(Span<float> rgba, float[]?[] tables)
        {
            for (int c = 0; c < 3; c++)
            {
                var table = tables[c]!;
                for (int i = c; i < rgba.Length; i += 4)
                {
                    rgba[i] = LookupOne(rgba[i], table);
                }
            }
        }

        private static float LookupOne(float x, float[] table)
        {
            const float Last = TableSize - 1;
            x = x > 0f ? (x < 1f ? x : 1f) : 0f;
            float position = x * Last;
            int index = (int)position;
            if (index >= TableSize - 1)
            {
                return table[TableSize - 1];
            }

            float fraction = position - index;
            return table[index] + ((table[index + 1] - table[index]) * fraction);
        }

        public void ToLinear(Span<float> r, Span<float> g, Span<float> b)
        {
            if (decodeTables is null)
            {
                ColorKernels.ToLinear(transfer, gamma, r);
                ColorKernels.ToLinear(transfer, gamma, g);
                ColorKernels.ToLinear(transfer, gamma, b);
                return;
            }

            Lookup(r, decodeTables[0]!);
            Lookup(g, decodeTables[1]!);
            Lookup(b, decodeTables[2]!);
        }

        public void FromLinear(Span<float> r, Span<float> g, Span<float> b)
        {
            if (encodeTables is null)
            {
                ColorKernels.FromLinear(transfer, gamma, r);
                ColorKernels.FromLinear(transfer, gamma, g);
                ColorKernels.FromLinear(transfer, gamma, b);
                return;
            }

            Lookup(r, encodeTables[0]!);
            Lookup(g, encodeTables[1]!);
            Lookup(b, encodeTables[2]!);
        }

        // Tables cover 0..1; the input is clamped there (a table curve says nothing outside its domain) and NaN becomes 0.
        private static void Lookup(Span<float> samples, float[] table)
        {
            const float Last = TableSize - 1;
            for (int i = 0; i < samples.Length; i++)
            {
                float x = samples[i];
                x = x > 0f ? (x < 1f ? x : 1f) : 0f;
                float position = x * Last;
                int index = (int)position;
                if (index >= TableSize - 1)
                {
                    samples[i] = table[TableSize - 1];
                    continue;
                }

                float fraction = position - index;
                samples[i] = table[index] + ((table[index + 1] - table[index]) * fraction);
            }
        }

        private static float[] BuildDecodeTable(IccCurve curve)
        {
            var table = new float[TableSize];
            for (int i = 0; i < TableSize; i++)
            {
                table[i] = (float)curve.Lookup(i / (double)(TableSize - 1));
            }

            return table;
        }

        // Inverts a monotonic tone curve by bisection, once per table entry.
        private static float[] BuildEncodeTable(IccCurve curve)
        {
            var table = new float[TableSize];
            for (int i = 0; i < TableSize; i++)
            {
                double target = i / (double)(TableSize - 1);
                double lo = 0, hi = 1;
                for (int step = 0; step < 40; step++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (curve.Lookup(mid) < target)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                table[i] = (float)((lo + hi) * 0.5);
            }

            return table;
        }
    }
}
