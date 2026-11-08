using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PeachImage.Formats.Webp.Decoding.Vp8.Dct;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary><see cref="Vp8VectorInverseDct.AddDcOnly"/> against the scalar DC-only kernel it replaces, over every kind of DC and pixel content.</summary>
public class Vp8DcOnlyAddTests
{
    [Fact]
    public void AddDcOnly_MatchesScalarKernel_ForExtremeAndRandomDcValues()
    {
        Assert.SkipUnless(Vector128.IsHardwareAccelerated, "Vector128 is not hardware accelerated here.");

        const int stride = 11;
        var random = new Random(404);
        short[] dcValues = [short.MinValue, -32000, -4096, -33, -9, -4, -1, 0, 1, 3, 4, 5, 12, 255 * 8, 4095, 32000, short.MaxValue];

        foreach (short dc in dcValues.Concat(Enumerable.Range(0, 300).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1))))
        {
            foreach (int fill in new[] { -1, 0, 255, 128 })
            {
                byte[] plane = new byte[stride * 6];
                for (int i = 0; i < plane.Length; i++)
                {
                    plane[i] = fill >= 0 ? (byte)fill : (byte)random.Next(256);
                }

                short[] coefficients = new short[16];
                coefficients[0] = dc;

                byte[] expected = (byte[])plane.Clone();
                Vp8ScalarInverseDct.TransformDcOnlyAndAdd(coefficients, expected, stride + 2, stride);

                byte[] actual = (byte[])plane.Clone();
                Vp8VectorInverseDct.AddDcOnly(
                    ref MemoryMarshal.GetArrayDataReference(coefficients),
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(actual), stride + 2),
                    stride);

                Assert.Equal(expected, actual);
            }
        }
    }
}
