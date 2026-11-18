using System.Runtime.Intrinsics;
using PeachImage.Formats.Shared.Arithmetic;
using PeachImage.Formats.Shared.Vectors;

namespace PeachImage.Tests.Vectors;

/// <summary>
/// Checks <see cref="VectorShuffle"/> and <see cref="IntMath"/> against scalar definitions. Run on both target
/// frameworks: net11.0 exercises the BCL forwards, net10.0 the hand-written fallbacks.
/// </summary>
public class VectorShuffleTests
{
    [Fact]
    public void Vector128OperationsMatchScalarDefinitionsAtEveryElementSize()
    {
        Assert.SkipUnless(VectorShuffle.IsSupported, "No two-vector interleave is available here.");
        var random = new Random(1234);
        for (int trial = 0; trial < 64; trial++)
        {
            var a = RandomVector128(random);
            var b = RandomVector128(random);
            Check128<byte>(a, b);
            Check128<ushort>(a.AsUInt16(), b.AsUInt16());
            Check128<int>(a.AsInt32(), b.AsInt32());
            Check128<ulong>(a.AsUInt64(), b.AsUInt64());
        }
    }

    [Fact]
    public void Vector256OperationsMatchScalarDefinitionsAtEveryElementSize()
    {
        Assert.SkipUnless(VectorShuffle.IsSupported256, "No 256-bit permutes are available here.");
        var random = new Random(5678);
        for (int trial = 0; trial < 64; trial++)
        {
            var a = Vector256.Create(RandomVector128(random), RandomVector128(random));
            var b = Vector256.Create(RandomVector128(random), RandomVector128(random));
            Check256<byte>(a, b);
            Check256<short>(a.AsInt16(), b.AsInt16());
            Check256<uint>(a.AsUInt32(), b.AsUInt32());
            Check256<long>(a.AsInt64(), b.AsInt64());
        }
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(7, 2)]
    [InlineData(8, 2)]
    [InlineData(-7, 2)]
    [InlineData(-8, 2)]
    [InlineData(7, -2)]
    [InlineData(-7, -2)]
    [InlineData(int.MaxValue, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, 3)]
    [InlineData(1, int.MaxValue)]
    public void CeilDivMatchesMathematicalCeiling(int a, int b)
    {
        int expected = (int)Math.Ceiling((decimal)a / b);
        Assert.Equal(expected, IntMath.CeilDiv(a, b));
        Assert.Equal((long)expected, IntMath.CeilDiv((long)a, b));
    }

    private static Vector128<byte> RandomVector128(Random random)
    {
        Span<byte> bytes = stackalloc byte[16];
        random.NextBytes(bytes);
        return Vector128.Create<byte>(bytes);
    }

    private static void Check128<T>(Vector128<T> a, Vector128<T> b)
    {
        int n = Vector128<T>.Count;
        int half = n / 2;
        T[] zipLower = new T[n], zipUpper = new T[n];
        T[] concatLL = new T[n], concatUU = new T[n], reverse = new T[n];
        for (int i = 0; i < half; i++)
        {
            zipLower[2 * i] = a[i];
            zipLower[(2 * i) + 1] = b[i];
            zipUpper[2 * i] = a[half + i];
            zipUpper[(2 * i) + 1] = b[half + i];
            concatLL[i] = a[i];
            concatLL[half + i] = b[i];
            concatUU[i] = a[half + i];
            concatUU[half + i] = b[half + i];
        }

        for (int i = 0; i < n; i++)
        {
            reverse[i] = a[n - 1 - i];
        }

        Assert.Equal(zipLower, ToArray(VectorShuffle.ZipLower(a, b)));
        Assert.Equal(zipUpper, ToArray(VectorShuffle.ZipUpper(a, b)));
        Assert.Equal(concatLL, ToArray(VectorShuffle.ConcatLowerLower(a, b)));
        Assert.Equal(concatUU, ToArray(VectorShuffle.ConcatUpperUpper(a, b)));
        Assert.Equal(reverse, ToArray(VectorShuffle.Reverse(a)));
    }

    private static void Check256<T>(Vector256<T> a, Vector256<T> b)
    {
        int n = Vector256<T>.Count;
        int half = n / 2;
        T[] concatLL = new T[n], concatLU = new T[n], concatUU = new T[n];
        for (int i = 0; i < half; i++)
        {
            concatLL[i] = a[i];
            concatLL[half + i] = b[i];
            concatLU[i] = a[i];
            concatLU[half + i] = b[half + i];
            concatUU[i] = a[half + i];
            concatUU[half + i] = b[half + i];
        }

        Assert.Equal(concatLL, ToArray(VectorShuffle.ConcatLowerLower(a, b)));
        Assert.Equal(concatLU, ToArray(VectorShuffle.ConcatLowerUpper(a, b)));
        Assert.Equal(concatUU, ToArray(VectorShuffle.ConcatUpperUpper(a, b)));
    }

    private static T[] ToArray<T>(Vector128<T> v)
    {
        var result = new T[Vector128<T>.Count];
        v.CopyTo(result);
        return result;
    }

    private static T[] ToArray<T>(Vector256<T> v)
    {
        var result = new T[Vector256<T>.Count];
        v.CopyTo(result);
        return result;
    }
}
