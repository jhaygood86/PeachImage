using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PeachImage.Formats.Png.Decoding;

namespace PeachImage.Tests.Formats.Png.Unit.BitDepth;

public class Png16BitByteSwapTests
{
    [Fact]
    public void SwapBigEndian16_KnownValues_MatchesExpected()
    {
        byte[] source = [0x01, 0x02, 0xFF, 0xFF, 0x00, 0x00, 0x7F, 0x80];
        var destination = new byte[source.Length];

        Png16BitByteSwap.SwapBigEndian16(source, destination);

        var samples = MemoryMarshal.Cast<byte, ushort>(destination);
        Assert.Equal([0x0102, 0xFFFF, 0x0000, 0x7F80], samples.ToArray());
    }

    [Fact]
    public void SwapBigEndian16_ExactlyOneVectorWidth_TakesSimdPathOnly()
    {
        AssertRoundTrips(sampleCount: 8); // 16 bytes: exactly one Vector128<byte> load, no scalar tail.
    }

    [Fact]
    public void SwapBigEndian16_OneSamplePastVectorWidth_ExercisesScalarTail()
    {
        AssertRoundTrips(sampleCount: 9); // 18 bytes: one SIMD vector plus a 2-byte (1-sample) scalar tail.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(100)]
    [InlineData(257)]
    public void SwapBigEndian16_ManyLengths_MatchesScalarReference(int sampleCount)
    {
        AssertRoundTrips(sampleCount);
    }

    private static void AssertRoundTrips(int sampleCount)
    {
        uint state = 0x9E3779B9u ^ (uint)sampleCount;
        var source = new byte[sampleCount * 2];
        for (int i = 0; i < source.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            source[i] = (byte)state;
        }

        var expected = new ushort[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            expected[i] = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(i * 2, 2));
        }

        var destination = new byte[source.Length];
        Png16BitByteSwap.SwapBigEndian16(source, destination);

        Assert.Equal(expected, MemoryMarshal.Cast<byte, ushort>(destination).ToArray());
    }
}
