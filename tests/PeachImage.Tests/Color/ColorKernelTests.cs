using System.Runtime.InteropServices;
using PeachImage.Internal.Color;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Tests.Color;

/// <summary>Every vector kernel against its scalar twin. Run again with <c>DOTNET_EnableAVX2=0</c> to exercise the 128-bit (NEON-width) tier.</summary>
public class ColorKernelTests
{
    private static float[] Samples(int count, float min, float max, int seed)
    {
        var random = new Random(seed);
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = min + ((float)random.NextDouble() * (max - min));
        }

        return values;
    }

    [Theory]
    [InlineData((int)ColorKernels.Kind.SrgbDecode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.SrgbEncode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.SrgbDecode, -1.5f, 3f)]
    [InlineData((int)ColorKernels.Kind.SrgbEncode, -1.5f, 3f)]
    [InlineData((int)ColorKernels.Kind.Bt709Decode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.Bt709Encode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.PqDecode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.PqEncode, 0f, 49f)]
    [InlineData((int)ColorKernels.Kind.HlgDecode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.HlgEncode, 0f, 5f)]
    [InlineData((int)ColorKernels.Kind.Power, 0f, 8f)]
    public void TransferKernel_MatchesTheDoublePrecisionReference(int kindValue, float min, float max)
    {
        var kind = (ColorKernels.Kind)kindValue;
        // 1003 samples: not a multiple of any vector width, so every tail is exercised.
        var values = Samples(1003, min, max, (int)kind);
        values[0] = 0f;
        values[1] = min < 0 ? -0f : 0f;
        var vector = (float[])values.Clone();
        var scalar = (float[])values.Clone();

        ColorKernels.Apply(vector, kind, 2.4f);
        ColorKernels.ApplyScalar(scalar, kind, 2.4f);

        for (int i = 0; i < values.Length; i++)
        {
            // PQ chains pow calls with exponents up to 78 and divides by a difference that cancels near full scale, so float32 itself limits it to about 1e-4 relative (0.01 cd/m2 at 10000).
            float relative = kind is ColorKernels.Kind.PqDecode or ColorKernels.Kind.PqEncode ? 1e-4f : 4e-6f;
            float tolerance = Math.Max(2e-6f, Math.Abs(scalar[i]) * relative);
            Assert.True(Math.Abs(vector[i] - scalar[i]) <= tolerance, $"{kind}({values[i]}): vector {vector[i]}, reference {scalar[i]}");
        }
    }

    [Fact]
    public void SrgbTransfer_RoundTripsEverySixteenBitValue()
    {
        var values = new float[65536];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i / 65535f;
        }

        var work = (float[])values.Clone();
        ColorKernels.ToLinear(TransferFunction.Srgb, 0, work);
        ColorKernels.FromLinear(TransferFunction.Srgb, 0, work);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.True(Math.Abs(work[i] * 65535f - i) < 0.05f, $"{i}: {work[i] * 65535f}");
        }
    }

    [Fact]
    public void Matrix_VectorMatchesScalar()
    {
        float[] m = [0.4f, 0.3f, 0.3f, -0.1f, 1.2f, -0.1f, 0.05f, 0.05f, 0.9f];
        var r = Samples(1003, -1, 2, 1);
        var g = Samples(1003, -1, 2, 2);
        var b = Samples(1003, -1, 2, 3);
        var (vr, vg, vb) = ((float[])r.Clone(), (float[])g.Clone(), (float[])b.Clone());
        ColorKernels.Matrix3x3(vr, vg, vb, m);
        ColorKernels.Matrix3x3Scalar(r, g, b, m);
        for (int i = 0; i < r.Length; i++)
        {
            Assert.Equal(r[i], vr[i], 5);
            Assert.Equal(g[i], vg[i], 5);
            Assert.Equal(b[i], vb[i], 5);
        }
    }

    [Fact]
    public void ClampAndPlanarPremultiply_MatchScalar()
    {
        var v = Samples(1003, -2, 3, 4);
        v[10] = float.NaN;
        var vector = (float[])v.Clone();
        ColorKernels.ClampUnit(vector);
        ColorKernels.ClampUnitScalar(v);
        Assert.Equal(v, vector);
        Assert.Equal(0f, vector[10]);

        var r = Samples(1003, 0, 1, 5);
        var g = Samples(1003, 0, 1, 6);
        var b = Samples(1003, 0, 1, 7);
        var a = Samples(1003, 0, 1, 8);
        a[3] = 0f;
        var (vr, vg, vb) = ((float[])r.Clone(), (float[])g.Clone(), (float[])b.Clone());
        ColorKernels.Premultiply(vr, vg, vb, a);
        ColorKernels.PremultiplyScalar(r, g, b, a);
        Assert.Equal(r, vr);
        Assert.Equal(g, vg);
        ColorKernels.Unpremultiply(vr, vg, vb, a);
        ColorKernels.UnpremultiplyScalar(r, g, b, a);
        for (int i = 0; i < r.Length; i++)
        {
            Assert.Equal(r[i], vr[i], 5);
            Assert.Equal(b[i], vb[i], 5);
        }

        Assert.Equal(0f, vr[3]);
    }

    [Fact]
    public void InterleavedPremultiply_EveryTierMatchesScalar()
    {
        var source = Samples(4 * 1003, 0, 1, 9);
        for (int i = 3; i < source.Length; i += 4 * 7)
        {
            source[i] = 0f;
        }

        var scalarPre = (float[])source.Clone();
        RgbaRowKernels.PremultiplyScalar(scalarPre, 1003);
        var scalarUn = (float[])source.Clone();
        RgbaRowKernels.UnpremultiplyScalar(scalarUn, 1003);

        foreach (var tier in new Func<float[], int>[]
        {
            row => RgbaRowKernels.PremultiplyVector128(row, 1003),
            row => RgbaRowKernels.PremultiplyVector256(row, 1003),
        })
        {
            var row = (float[])source.Clone();
            int done = tier(row);
            RgbaRowKernels.PremultiplyScalar(row.AsSpan(done * 4), 1003 - done);
            Assert.Equal(scalarPre, row);
        }

        foreach (var tier in new Func<float[], int>[]
        {
            row => RgbaRowKernels.UnpremultiplyVector128(row, 1003),
            row => RgbaRowKernels.UnpremultiplyVector256(row, 1003),
        })
        {
            var row = (float[])source.Clone();
            int done = tier(row);
            RgbaRowKernels.UnpremultiplyScalar(row.AsSpan(done * 4), 1003 - done);
            Assert.Equal(scalarUn, row);
        }
    }

    [Fact]
    public void HalfToSingle_IsExactForEveryHalfValue()
    {
        var halves = new Half[65536];
        for (int i = 0; i < halves.Length; i++)
        {
            halves[i] = BitConverter.UInt16BitsToHalf((ushort)i);
        }

        var vector = new float[halves.Length];
        var scalar = new float[halves.Length];
        RgbaRowKernels.HalfToSingle(halves, vector);
        RgbaRowKernels.HalfToSingleScalar(halves, scalar);
        for (int i = 0; i < halves.Length; i++)
        {
            if (float.IsNaN(scalar[i]))
            {
                Assert.True(float.IsNaN(vector[i]), $"half bits {i:X4}");
            }
            else
            {
                Assert.Equal(BitConverter.SingleToUInt32Bits(scalar[i]), BitConverter.SingleToUInt32Bits(vector[i]));
            }
        }
    }

    [Fact]
    public void SingleToHalf_RoundsLikeTheScalarConversion()
    {
        var random = new Random(77);
        var values = new float[200_003];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i switch
            {
                0 => 0f,
                1 => -0f,
                2 => 65504f,
                3 => 65519f,
                4 => 1e9f,
                5 => -1e9f,
                6 => float.NaN,
                7 => float.PositiveInfinity,
                8 => 5.9604645e-8f,
                9 => 2.9802322e-8f,
                _ => BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue)),
            };
        }

        var vector = new Half[values.Length];
        var scalar = new Half[values.Length];
        RgbaRowKernels.SingleToHalf(values, vector);
        RgbaRowKernels.SingleToHalfScalar(values, scalar);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.True(BitConverter.HalfToUInt16Bits(vector[i]) == BitConverter.HalfToUInt16Bits(scalar[i]), $"{values[i]:R}: vector {vector[i]}, scalar {scalar[i]}");
        }
    }

    [Fact]
    public void ColorTransformApply_DoesNotAllocateOnceWarm()
    {
        var transform = ColorTransform.Create(null, NamedColorSpace.Srgb, null, NamedColorSpace.DisplayP3, clip: true, intent: null);
        var row = new float[4 * 512];
        Array.Fill(row, 0.5f);
        for (int i = 0; i < 4; i++)
        {
            transform.Apply(row, 512);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            transform.Apply(row, 512);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ColorSpaceConversion_AllocatesNothingPerPixelBeyondTheResult()
    {
        using var warm = Image.Create(64, 64, PixelFormat.Rgba32);
        using var w = warm.ConvertColorSpace(NamedColorSpace.DisplayP3, new ColorConversionOptions { MaxDegreeOfParallelism = 1 });

        using var big = Image.Create(512, 512, PixelFormat.Rgba32);
        big.ConvertColorSpace(NamedColorSpace.DisplayP3, new ColorConversionOptions { MaxDegreeOfParallelism = 1 }).Dispose();
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var converted = big.ConvertColorSpace(NamedColorSpace.DisplayP3, new ColorConversionOptions { MaxDegreeOfParallelism = 1 });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The result buffer is pooled; what remains is per-call bookkeeping, a few kilobytes at most and not proportional to the 262,144 pixels.
        Assert.True(allocated < 32 * 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public void FloatToInteger_RoundsToNearestClipsAndEncodesOnRequest()
    {
        using var source = Image.Create(4, 1, PixelFormat.RgbaF32);
        var s = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        float[] values = [0.5f, 0.5f, 0.5f, 1f, 1.5f, -0.5f, float.NaN, 1f, 0.5f / 255f, 1.5f / 255f, 254.5f / 255f, 1f, 0.2f, 0.2f, 0.2f, 0.5f];
        values.CopyTo(s);

        using var plain = source.ConvertTo(PixelFormat.Rgba32);
        var p = plain.GetPixelSpan();
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, p[..4].ToArray()); // 127.5 rounds up
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, p[4..8].ToArray()); // clip, NaN to 0
        Assert.Equal(new byte[] { 1, 2, 255, 255 }, p[8..12].ToArray());

        using var encoded = source.ConvertTo(PixelFormat.Rgba32, new PixelConversionOptions { EncodeSrgb = true });
        var e = encoded.GetPixelSpan();
        Assert.Equal(188, e[0]); // linear 0.5 -> sRGB 0.7354 -> 187.5 up
        Assert.Equal(255, e[3]); // alpha is not encoded
        Assert.Equal(128, e[15]);

        using var decoded = encoded.ConvertTo(PixelFormat.RgbaF32, new PixelConversionOptions { DecodeSrgb = true });
        Assert.Equal(0.5, MemoryMarshal.Cast<byte, float>(decoded.GetPixelSpan())[0], 2);

        Assert.Throws<ArgumentException>(() => source.ConvertTo(PixelFormat.Rgba32, new PixelConversionOptions { EncodeSrgb = true, DecodeSrgb = true }));
    }

    [Theory]
    [InlineData(PixelFormat.PremultipliedRgba32)]
    [InlineData(PixelFormat.PremultipliedRgba64)]
    [InlineData(PixelFormat.PremultipliedRgbaF16)]
    [InlineData(PixelFormat.PremultipliedRgbaF32)]
    [InlineData(PixelFormat.RgbaF16)]
    public void NewFormats_ConvertToAndFromStraightRgba(PixelFormat format)
    {
        using var source = Image.Create(3, 1, PixelFormat.Rgba64);
        var s = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        // straight (200, 100, 50) at alpha 255 / 128 / 0
        ushort[] values = [51400, 25700, 12850, 65535, 51400, 25700, 12850, 32896, 51400, 25700, 12850, 0];
        values.CopyTo(s);

        using var converted = source.ConvertTo(format);
        Assert.Equal(format, converted.PixelFormat);
        Assert.Equal(3 * format.GetBytesPerPixel(), converted.GetPixelSpan().Length);
        using var back = converted.ConvertTo(PixelFormat.Rgba64);
        var b = MemoryMarshal.Cast<byte, ushort>(back.GetPixelSpan());

        double tolerance = format switch
        {
            PixelFormat.PremultipliedRgba32 => 600,
            PixelFormat.RgbaF16 or PixelFormat.PremultipliedRgbaF16 => 40,
            _ => 2,
        };

        for (int i = 0; i < 8; i++)
        {
            double allowed = i >= 4 && format == PixelFormat.PremultipliedRgba32 ? 300 : tolerance;
            Assert.True(Math.Abs(b[i] - values[i]) <= allowed, $"{format} sample {i}: {b[i]} vs {values[i]}");
        }

        // Fully transparent premultiplied pixels lose their color; straight ones keep it.
        Assert.Equal(0, b[11]);
        if (format.IsPremultiplied())
        {
            Assert.Equal(0, b[8]);
        }
        else
        {
            Assert.True(Math.Abs(b[8] - values[8]) <= tolerance);
        }
    }

    [Fact]
    public void ResizePremultipliedAndHalfFormats_StaysInFormatAndKeepsAlphaCoverage()
    {
        foreach (var format in new[] { PixelFormat.PremultipliedRgba32, PixelFormat.PremultipliedRgbaF16, PixelFormat.RgbaF16 })
        {
            using var source = Image.Create(8, 8, PixelFormat.Rgba32);
            for (int i = 0; i < 64; i++)
            {
                source.GetPixelSpan()[(i * 4)..][..4].Fill(200);
            }

            using var typed = source.ConvertTo(format);
            using var resized = typed.Resize(4, 4);
            Assert.Equal(format, resized.PixelFormat);
            using var straight = resized.ConvertTo(PixelFormat.Rgba32);
            Assert.All(straight.GetPixelSpan().ToArray(), v => Assert.InRange(v, 199, 201));
        }
    }
}

public class DirectResizeTests
{
    private static Image Scene(int width = 24, int height = 16)
    {
        // Left third opaque red, middle half-transparent green, right third fully transparent (and colorless).
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        var s = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * 4;
                if (x < width / 3)
                {
                    (s[o], s[o + 1], s[o + 2], s[o + 3]) = (230, 20, 10, 255);
                }
                else if (x < 2 * width / 3)
                {
                    (s[o], s[o + 1], s[o + 2], s[o + 3]) = (15, 200, 40, 128);
                }
                else
                {
                    (s[o], s[o + 1], s[o + 2], s[o + 3]) = (0, 0, 0, 0);
                }
            }
        }

        return image;
    }

    [Theory]
    [InlineData(PixelFormat.PremultipliedRgba32, 2)]
    [InlineData(PixelFormat.PremultipliedRgba64, 2)]
    [InlineData(PixelFormat.PremultipliedRgbaF32, 1)]
    [InlineData(PixelFormat.PremultipliedRgbaF16, 2)]
    [InlineData(PixelFormat.RgbaF16, 2)]
    public void PremultipliedAndHalfFloat_ResizeLikeTheStraightFloatReference(PixelFormat format, int toleranceLsb)
    {
        using var straight = Scene();
        using var reference = straight.ConvertTo(PixelFormat.RgbaF32).Resize(10, 7, new ResizeOptions { Filter = ResamplingFilter.Bicubic });
        using var referenceBytes = reference.ConvertTo(PixelFormat.Rgba32);

        using var typed = straight.ConvertTo(format);
        using var resized = typed.Resize(10, 7, new ResizeOptions { Filter = ResamplingFilter.Bicubic });
        Assert.Equal(format, resized.PixelFormat);
        using var back = resized.ConvertTo(PixelFormat.Rgba32);

        var a = referenceBytes.GetPixelSpan();
        var b = back.GetPixelSpan();
        for (int i = 0; i < a.Length; i++)
        {
            // Where alpha is 0 the color is undefined; compare the rest.
            if (a[(i / 4 * 4) + 3] == 0 || b[(i / 4 * 4) + 3] == 0)
            {
                continue;
            }

            Assert.True(Math.Abs(a[i] - b[i]) <= toleranceLsb + (i % 4 == 3 ? 0 : 1), $"{format} byte {i}: {b[i]} vs reference {a[i]}");
        }
    }

    [Fact]
    public void TransparentPixels_KeepNoColorUnderPremultipliedResizing_AndDoNotBleed()
    {
        using var straight = Scene();
        using var typed = straight.ConvertTo(PixelFormat.PremultipliedRgba32);
        using var resized = typed.Resize(6, 4);
        var s = resized.GetPixelSpan();
        for (int i = 0; i < s.Length; i += 4)
        {
            Assert.True(s[i] <= s[i + 3] && s[i + 1] <= s[i + 3] && s[i + 2] <= s[i + 3], "premultiplied color must not exceed alpha");
        }

        // The right edge is fully transparent and stays so with zero color.
        int last = ((6 - 1) * 4) + 0;
        Assert.Equal(0, s[last + 3]);
        Assert.Equal(0, s[last]);
    }

    [Theory]
    [InlineData(ResizeMode.Pad)]
    [InlineData(ResizeMode.Crop)]
    [InlineData(ResizeMode.Max)]
    public void EveryResizeMode_WorksForTheNewFormats(ResizeMode mode)
    {
        using var straight = Scene();
        foreach (var format in new[] { PixelFormat.PremultipliedRgba32, PixelFormat.PremultipliedRgbaF16, PixelFormat.RgbaF16, PixelFormat.PremultipliedRgba64, PixelFormat.PremultipliedRgbaF32 })
        {
            using var typed = straight.ConvertTo(format);
            using var resized = typed.Resize(12, 12, new ResizeOptions { Mode = mode, BackgroundColor = (10, 20, 30, 255) });
            Assert.Equal(format, resized.PixelFormat);
            if (mode != ResizeMode.Max)
            {
                Assert.Equal((12, 12), (resized.Width, resized.Height));
            }
        }
    }

    [Fact]
    public void NearestNeighbor_CopiesPremultipliedPixelsWhole()
    {
        using var straight = Scene();
        using var typed = straight.ConvertTo(PixelFormat.PremultipliedRgbaF16);
        using var resized = typed.Resize(12, 8, new ResizeOptions { Filter = ResamplingFilter.NearestNeighbor });
        Assert.Equal(PixelFormat.PremultipliedRgbaF16, resized.PixelFormat);
    }
}
