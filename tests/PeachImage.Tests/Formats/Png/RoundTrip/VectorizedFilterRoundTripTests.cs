using System.Runtime.InteropServices;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.RoundTrip;

/// <summary>
/// Round-trips large images (well above the SIMD-worthwhile size threshold) through every fixed filter
/// strategy, so each of the 5 filter types' vectorized encode path is actually exercised — the default
/// Adaptive strategy used elsewhere doesn't guarantee any particular filter type gets picked, and small
/// test images elsewhere in this suite fall under the vectorization threshold entirely.
/// </summary>
public class VectorizedFilterRoundTripTests
{
    private const int Width = 337; // deliberately not a multiple of any common SIMD width, to exercise the scalar epilogue too.
    private const int Height = 41;

    public static IEnumerable<object[]> AllFixedStrategies() =>
    [
        [PngFilterStrategy.FixedNone],
        [PngFilterStrategy.FixedSub],
        [PngFilterStrategy.FixedUp],
        [PngFilterStrategy.FixedAverage],
        [PngFilterStrategy.FixedPaeth],
        [PngFilterStrategy.Adaptive],
    ];

    [Theory]
    [MemberData(nameof(AllFixedStrategies))]
    public void Rgb24_RoundTrips_Exactly(PngFilterStrategy strategy)
    {
        using var source = CreateNoisyRgb24(Width, Height);
        using var decoded = EncodeThenDecode(source, strategy);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [MemberData(nameof(AllFixedStrategies))]
    public void Rgba32_RoundTrips_Exactly(PngFilterStrategy strategy)
    {
        using var source = CreateNoisyImage(Width, Height, PixelFormat.Rgba32);
        using var decoded = EncodeThenDecode(source, strategy);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [MemberData(nameof(AllFixedStrategies))]
    public void Gray8_RoundTrips_Exactly(PngFilterStrategy strategy)
    {
        using var source = CreateNoisyImage(Width, Height, PixelFormat.Gray8);
        using var decoded = EncodeThenDecode(source, strategy);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [MemberData(nameof(AllFixedStrategies))]
    public void Rgb48_RoundTrips_Exactly(PngFilterStrategy strategy)
    {
        using var source = CreateNoisy16Bit(Width, Height, PixelFormat.Rgb48);
        using var decoded = EncodeThenDecode(source, strategy);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [MemberData(nameof(AllFixedStrategies))]
    public void Rgba64_RoundTrips_Exactly(PngFilterStrategy strategy)
    {
        using var source = CreateNoisy16Bit(Width, Height, PixelFormat.Rgba64);
        using var decoded = EncodeThenDecode(source, strategy);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [MemberData(nameof(AllFixedStrategies))]
    public void Gray16_RoundTrips_Exactly(PngFilterStrategy strategy)
    {
        using var source = CreateNoisy16Bit(Width, Height, PixelFormat.Gray16);
        using var decoded = EncodeThenDecode(source, strategy);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    /// <summary>All-edge-case pixel values (0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF repeating), specifically to stress the Paeth/Average SIMD math's boundary behavior (wraparound, sign edge cases in the widened Paeth predictor).</summary>
    [Theory]
    [InlineData(PngFilterStrategy.FixedAverage)]
    [InlineData(PngFilterStrategy.FixedPaeth)]
    public void EdgeCaseByteValues_RoundTripExactly(PngFilterStrategy strategy)
    {
        using var source = Image.Create(Width, Height, PixelFormat.Rgb24);
        byte[] edgeValues = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];
        var span = source.GetPixelSpan();
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = edgeValues[i % edgeValues.Length];
        }

        using var decoded = EncodeThenDecode(source, strategy);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    private static Image EncodeThenDecode(Image source, PngFilterStrategy strategy)
    {
        using var ms = new MemoryStream();
        PngEncoder.Encode(source, ms, new PngEncoderOptions { FilterStrategy = strategy });
        ms.Position = 0;
        return PngDecoder.Decode(ms);
    }

    private static Image CreateNoisyRgb24(int width, int height) => CreateNoisyImage(width, height, PixelFormat.Rgb24);

    private static Image CreateNoisyImage(int width, int height, PixelFormat format)
    {
        var image = Image.Create(width, height, format);
        var span = image.GetPixelSpan();
        // Deterministic pseudo-random byte stream (not System.Random, to keep the test reproducible
        // without relying on a fixed seed's cross-runtime stability) that still covers the full byte range.
        uint state = 0x9E3779B9;
        for (int i = 0; i < span.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            span[i] = (byte)state;
        }

        return image;
    }

    private static Image CreateNoisy16Bit(int width, int height, PixelFormat format)
    {
        var image = Image.Create(width, height, format);
        var samples = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        uint state = 0x2545F491;
        for (int i = 0; i < samples.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            samples[i] = (ushort)state;
        }

        return image;
    }
}
