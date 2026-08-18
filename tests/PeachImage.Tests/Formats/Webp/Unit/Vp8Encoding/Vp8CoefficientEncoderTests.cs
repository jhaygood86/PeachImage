using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// Validates <see cref="Vp8CoefficientEncoder"/> by round-tripping through the real, unmodified
/// <see cref="Vp8CoefficientDecoder.DecodeBlock"/> (via the real <see cref="Vp8BoolDecoder"/>): whatever this
/// encoder writes for a given (zigzag-order, quantized-level) coefficient block must decode back to the exact
/// same block. Quant steps are fixed at 1 throughout so decoded (dequantized) values equal the quantized levels
/// directly, keeping assertions simple.
/// </summary>
public class Vp8CoefficientEncoderTests
{
    private static readonly byte[] Probabilities = Vp8CoefficientProbabilities.DefaultFlat;

    /// <summary>Encodes <paramref name="quantized"/> (zigzag order) then decodes it back with the real decoder, asserting the natural-order dequantized output and the returned scan position both match.</summary>
    private static void AssertRoundTrips(int planeType, int firstContext, int first, short[] quantized, int last)
    {
        var bw = new Vp8BoolEncoder();
        Vp8CoefficientEncoder.EncodeBlock(bw, Probabilities, planeType, firstContext, first, quantized, last);
        byte[] encoded = bw.Finish();

        var decoder = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        Span<short> output = stackalloc short[16];
        int decodedLast = Vp8CoefficientDecoder.DecodeBlock(decoder, Probabilities, planeType, firstContext, first, dcQuant: 1, acQuant: 1, output);

        Assert.Equal(last, decodedLast);

        for (int scan = 0; scan < 16; scan++)
        {
            int naturalPos = Vp8ZigZag.Order[scan];

            // Positions before `first` are never touched by either side (e.g. a non-B_PRED luma block's DC,
            // handled separately via the Y2/WHT path) -- the decoder leaves them at whatever the caller's
            // output buffer already held, which here is 0 (a fresh stackalloc).
            short expected = scan >= first && scan < last ? quantized[scan] : (short)0;
            Assert.True(expected == output[naturalPos], $"Scan {scan} (natural {naturalPos}): expected {expected}, got {output[naturalPos]}.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EncodeBlock_AllZeroBlock_RoundTrips(int planeType)
    {
        var quantized = new short[16];
        AssertRoundTrips(planeType, firstContext: 0, first: 0, quantized, last: 0);
    }

    [Fact]
    public void EncodeBlock_OnlyDcNonZero_RoundTrips()
    {
        short[] quantized = new short[16];
        quantized[0] = 7;
        AssertRoundTrips(planeType: 0, firstContext: 0, first: 0, quantized, last: 1);
    }

    [Fact]
    public void EncodeBlock_FirstOne_SkipsDcPosition_RoundTrips()
    {
        // planeType 0 with first=1 mirrors a non-B_PRED luma block: position 0 (DC) is never touched.
        short[] quantized = new short[16];
        quantized[0] = 99; // Irrelevant -- must never be read since first=1.
        quantized[3] = -5;
        AssertRoundTrips(planeType: 0, firstContext: 0, first: 1, quantized, last: 4);
    }

    [Fact]
    public void EncodeBlock_FullBlockAllSixteenNonZero_RoundTrips()
    {
        var quantized = new short[16];
        for (int i = 0; i < 16; i++)
        {
            quantized[i] = (short)(i % 2 == 0 ? i + 1 : -(i + 1));
        }

        AssertRoundTrips(planeType: 2, firstContext: 0, first: 0, quantized, last: 16);
    }

    [Fact]
    public void EncodeBlock_ZeroRunBetweenNonZeroCoefficients_RoundTrips()
    {
        var quantized = new short[16];
        quantized[0] = 3;
        quantized[5] = 1;
        quantized[6] = -2;
        AssertRoundTrips(planeType: 1, firstContext: 0, first: 0, quantized, last: 7);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void EncodeBlock_DifferentFirstContexts_RoundTrip(int firstContext, int unused)
    {
        _ = unused;
        var quantized = new short[16];
        quantized[0] = 1;
        quantized[1] = 1;
        AssertRoundTrips(planeType: 0, firstContext, first: 0, quantized, last: 2);
    }

    /// <summary>Sweeps every magnitude-category boundary the token cascade branches on (1, 2-4, cat1 5-6, cat2 7-10, cat3 11-18, cat4 19-34, cat5 35-66, cat6 67+), both signs.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(-2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(-6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(11)]
    [InlineData(18)]
    [InlineData(-18)]
    [InlineData(19)]
    [InlineData(34)]
    [InlineData(-34)]
    [InlineData(35)]
    [InlineData(66)]
    [InlineData(-66)]
    [InlineData(67)]
    [InlineData(130)]
    [InlineData(500)]
    [InlineData(2047)]
    [InlineData(-2047)]
    public void EncodeBlock_EveryMagnitudeCategory_RoundTrips(int value)
    {
        var quantized = new short[16];
        quantized[0] = (short)value;
        AssertRoundTrips(planeType: 3, firstContext: 0, first: 0, quantized, last: 1);
    }

    [Fact]
    public void EncodeBlock_RandomSparseBlocks_RoundTrip()
    {
        var random = new Random(42);
        for (int trial = 0; trial < 100; trial++)
        {
            var quantized = new short[16];
            int last = 0;
            for (int i = 0; i < 16; i++)
            {
                if (random.NextDouble() < 0.4)
                {
                    int magnitude = random.Next(1, 200);
                    quantized[i] = (short)(random.Next(2) == 0 ? magnitude : -magnitude);
                    last = i + 1;
                }
            }

            int planeType = random.Next(4);
            int firstContext = random.Next(3);
            AssertRoundTrips(planeType, firstContext, first: 0, quantized, last);
        }
    }
}
