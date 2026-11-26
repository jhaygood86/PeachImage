using PeachImage.Internal.Color;
using PeachImage.Internal.PixelFormatConversion;

namespace PeachImage.Tests.Color;

/// <summary>The interleaved-row kernels against their scalar twins. Run again with <c>DOTNET_EnableAVX2=0</c> for the 128-bit (NEON-width) tier.</summary>
public class InterleavedKernelTests
{
    private static float[] Rgba(int pixels, float min, float max, int seed)
    {
        var random = new Random(seed);
        var v = new float[pixels * 4];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = min + ((float)random.NextDouble() * (max - min));
        }

        return v;
    }

    [Theory]
    [InlineData((int)ColorKernels.Kind.SrgbDecode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.SrgbEncode, -1f, 3f)]
    [InlineData((int)ColorKernels.Kind.Bt709Decode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.Bt709Encode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.PqDecode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.HlgDecode, 0f, 1f)]
    [InlineData((int)ColorKernels.Kind.Power, 0f, 4f)]
    public void Transfer_MatchesScalar_AndNeverTouchesAlpha(int kindValue, float min, float max)
    {
        var kind = (ColorKernels.Kind)kindValue;
        var source = Rgba(509, min, max, kindValue);
        var vector = (float[])source.Clone();
        var scalar = (float[])source.Clone();
        InterleavedColorKernels.Transfer(vector, kind, 2.2f);
        InterleavedColorKernels.TransferScalar(scalar, kind, 2.2f);

        for (int i = 0; i < source.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.Equal(source[i], vector[i]);
                continue;
            }

            float relative = kind is ColorKernels.Kind.PqDecode or ColorKernels.Kind.PqEncode ? 1e-4f : 4e-6f;
            Assert.True(Math.Abs(vector[i] - scalar[i]) <= Math.Max(2e-6f, Math.Abs(scalar[i]) * relative), $"{kind}[{i}]: {vector[i]} vs {scalar[i]}");
        }
    }

    [Fact]
    public void Transfer_AgreesWithThePlanarKernels()
    {
        var source = Rgba(256, 0f, 1f, 9);
        var interleaved = (float[])source.Clone();
        InterleavedColorKernels.ToLinear(TransferFunction.Srgb, 0, interleaved);

        var r = new float[256];
        var g = new float[256];
        var b = new float[256];
        var a = new float[256];
        RgbaRowKernels.Deinterleave(source, r, g, b, a, 256);
        ColorKernels.ToLinear(TransferFunction.Srgb, 0, r);
        ColorKernels.ToLinear(TransferFunction.Srgb, 0, g);
        ColorKernels.ToLinear(TransferFunction.Srgb, 0, b);
        var planar = new float[source.Length];
        RgbaRowKernels.Interleave(r, g, b, a, planar, 256);
        Assert.Equal(planar, interleaved);
    }

    [Fact]
    public void Clamp_MatchesScalar_KeepsAlpha_AndZeroesNaN()
    {
        var source = Rgba(301, -2f, 3f, 4);
        source[10] = float.NaN;
        source[3] = 7f; // alpha is not clamped here
        var vector = (float[])source.Clone();
        var scalar = (float[])source.Clone();
        InterleavedColorKernels.ClampUnit(vector);
        InterleavedColorKernels.ClampUnitScalar(scalar);
        Assert.Equal(scalar, vector);
        Assert.Equal(0f, vector[10]);
        Assert.Equal(7f, vector[3]);
    }

    [Fact]
    public void Matrix_EveryTierMatchesScalar_AndKeepsAlpha()
    {
        float[] m = [0.4f, 0.3f, 0.3f, -0.1f, 1.2f, -0.1f, 0.05f, 0.05f, 0.9f];
        var source = Rgba(1003, -1f, 2f, 5);
        var expected = (float[])source.Clone();
        InterleavedColorKernels.Matrix3x3Scalar(expected, 1003, m);

        var v128 = (float[])source.Clone();
        int done128 = InterleavedColorKernels.Matrix3x3Vector128(v128, 1003, m);
        InterleavedColorKernels.Matrix3x3Scalar(v128.AsSpan(done128 * 4), 1003 - done128, m);

        var v256 = (float[])source.Clone();
        int done256 = InterleavedColorKernels.Matrix3x3Vector256(v256, 1003, m);
        InterleavedColorKernels.Matrix3x3Scalar(v256.AsSpan(done256 * 4), 1003 - done256, m);

        var dispatched = (float[])source.Clone();
        InterleavedColorKernels.Matrix3x3(dispatched, 1003, m);

        foreach (var result in new[] { v128, v256, dispatched })
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], result[i], 5);
                if (i % 4 == 3)
                {
                    Assert.Equal(source[i], result[i]);
                }
            }
        }
    }

    [Fact]
    public void InterleaveAndDeinterleave_AreExactInverses_OfTheScalarVersions()
    {
        const int pixels = 1003;
        var source = Rgba(pixels, 0f, 1f, 6);
        var (r, g, b, a) = (new float[pixels], new float[pixels], new float[pixels], new float[pixels]);
        var (sr, sg, sb, sa) = (new float[pixels], new float[pixels], new float[pixels], new float[pixels]);
        RgbaRowKernels.Deinterleave(source, r, g, b, a, pixels);
        RgbaRowKernels.DeinterleaveScalar(source, sr, sg, sb, sa, pixels);
        Assert.Equal(sr, r);
        Assert.Equal(sg, g);
        Assert.Equal(sb, b);
        Assert.Equal(sa, a);

        var joined = new float[source.Length];
        RgbaRowKernels.Interleave(r, g, b, a, joined, pixels);
        Assert.Equal(source, joined);
    }

    [Fact]
    public void FlatConversions_AreBitIdenticalToTheScalarOnes()
    {
        var random = new Random(8);
        var bytes = new byte[4099];
        random.NextBytes(bytes);
        var vector = new float[bytes.Length];
        var scalar = new float[bytes.Length];
        RgbaRowKernels.BytesToSingle(bytes, vector);
        RgbaRowKernels.BytesToSingleScalar(bytes, scalar);
        Assert.Equal(scalar, vector);

        var floats = new float[4099];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = i switch { 0 => float.NaN, 1 => -1f, 2 => 2f, 3 => 0.5f / 255f, 4 => 127.5f / 255f, _ => ((float)random.NextDouble() * 1.4f) - 0.2f };
        }

        var back = new byte[floats.Length];
        var backScalar = new byte[floats.Length];
        RgbaRowKernels.SingleToBytes(floats, back);
        RgbaRowKernels.SingleToBytesScalar(floats, backScalar);
        Assert.Equal(backScalar, back);

        var shorts = new ushort[4099];
        for (int i = 0; i < shorts.Length; i++)
        {
            shorts[i] = (ushort)random.Next(0, 65536);
        }

        var wide = new float[shorts.Length];
        var wideScalar = new float[shorts.Length];
        RgbaRowKernels.UShortsToSingle(shorts, wide);
        RgbaRowKernels.UShortsToSingleScalar(shorts, wideScalar);
        Assert.Equal(wideScalar, wide);

        var narrow = new ushort[floats.Length];
        var narrowScalar = new ushort[floats.Length];
        RgbaRowKernels.SingleToUShorts(floats, narrow);
        RgbaRowKernels.SingleToUShortsScalar(floats, narrowScalar);
        Assert.Equal(narrowScalar, narrow);
    }

    [Fact]
    public void InterleavedTransform_MatchesThePlanarOne_AndAllocatesNothing()
    {
        var transform = ColorTransform.Create(null, NamedColorSpace.Srgb, null, NamedColorSpace.DisplayP3, clip: true, intent: null);
        var source = Rgba(512, 0f, 1f, 11);
        var interleaved = (float[])source.Clone();
        transform.Apply(interleaved, 512);

        var (r, g, b, a) = (new float[512], new float[512], new float[512], new float[512]);
        RgbaRowKernels.Deinterleave(source, r, g, b, a, 512);
        transform.ApplyPlanes(r, g, b);
        var planar = new float[source.Length];
        RgbaRowKernels.Interleave(r, g, b, a, planar, 512);
        for (int i = 0; i < planar.Length; i++)
        {
            Assert.Equal(planar[i], interleaved[i], 5);
        }

        for (int i = 0; i < 4; i++)
        {
            transform.Apply(interleaved, 512);
        }

        // Another test running at the same time can empty the shared array pool once and force a one-off allocation here, so the claim is that
        // some round of 100 conversions allocates nothing, not that every round does.
        long fewest = long.MaxValue;
        for (int round = 0; round < 5 && fewest != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                transform.Apply(interleaved, 512);
            }

            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, fewest);
    }
}
