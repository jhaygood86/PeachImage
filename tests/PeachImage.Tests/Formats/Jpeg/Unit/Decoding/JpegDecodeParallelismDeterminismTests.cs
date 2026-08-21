using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Decoding;

/// <summary>
/// Guards <see cref="PeachImage.Formats.Jpeg.Decoding.FrameReconstructor"/>'s row/block-row parallelism
/// (IDCT, chroma upsampling, color conversion — all dispatched via <c>RowParallel</c>) against races: a
/// subtle row-partition bug (e.g. two partitions sharing scratch state) wouldn't reliably fail the round
/// trip tests elsewhere in this project, since those use loose PSNR/average-difference tolerances over
/// sparsely-sampled pixels — exactly the kind of check a small, localized corruption could slip through.
/// These tests instead decode the same bytes repeatedly (sequentially and concurrently) and assert
/// pixel-exact agreement every time, the same style as <c>EncodeDecodeRoundTripTests.RestartIntervals_DecodeIdenticallyToWithoutRestarts</c>.
/// Images are deliberately large enough (well past <c>RowParallel.MinRowsForParallel</c> = 64, both in
/// destination rows and in IDCT block-rows) that the parallel path actually engages, and use a noisy
/// (not solid-color) pattern so a race that corrupts a few pixels would actually change the output instead
/// of coincidentally matching a uniform fill.
/// </summary>
public class JpegDecodeParallelismDeterminismTests
{
    private const int RepeatCount = 20;
    private const int ConcurrentDecodeCount = 16;

    [Fact]
    public void YCbCrWithChromaUpsampling_RepeatedSequentialDecodes_AreBitIdentical()
    {
        byte[] jpegBytes = EncodeNoisyImage(width: 645, height: 517, PixelFormat.Rgb24, JpegChromaSubsampling.Yuv420);

        using var first = JpegDecoder.Decode(new MemoryStream(jpegBytes));
        byte[] expected = first.GetPixelSpan().ToArray();

        for (int i = 0; i < RepeatCount; i++)
        {
            using var decoded = JpegDecoder.Decode(new MemoryStream(jpegBytes));
            Assert.True(expected.AsSpan().SequenceEqual(decoded.GetPixelSpan()), $"Decode #{i} diverged from the first decode.");
        }
    }

    [Fact]
    public void Grayscale_RepeatedSequentialDecodes_AreBitIdentical()
    {
        byte[] jpegBytes = EncodeNoisyImage(width: 641, height: 513, PixelFormat.Gray8, subsampling: null);

        using var first = JpegDecoder.Decode(new MemoryStream(jpegBytes));
        byte[] expected = first.GetPixelSpan().ToArray();

        for (int i = 0; i < RepeatCount; i++)
        {
            using var decoded = JpegDecoder.Decode(new MemoryStream(jpegBytes));
            Assert.True(expected.AsSpan().SequenceEqual(decoded.GetPixelSpan()), $"Decode #{i} diverged from the first decode.");
        }
    }

    [Fact]
    public void YCbCrWithChromaUpsampling_ConcurrentDecodes_AllMatchTheSequentialDecode()
    {
        byte[] jpegBytes = EncodeNoisyImage(width: 645, height: 517, PixelFormat.Rgb24, JpegChromaSubsampling.Yuv420);

        using var reference = JpegDecoder.Decode(new MemoryStream(jpegBytes));
        byte[] expected = reference.GetPixelSpan().ToArray();

        var mismatches = new bool[ConcurrentDecodeCount];
        Parallel.For(0, ConcurrentDecodeCount, i =>
        {
            using var decoded = JpegDecoder.Decode(new MemoryStream(jpegBytes));
            mismatches[i] = !expected.AsSpan().SequenceEqual(decoded.GetPixelSpan());
        });

        Assert.DoesNotContain(true, mismatches);
    }

    private static byte[] EncodeNoisyImage(int width, int height, PixelFormat pixelFormat, JpegChromaSubsampling? subsampling)
    {
        using var source = Image.Create(width, height, pixelFormat);
        var pixels = source.GetPixelSpan();
        var random = new Random(12345);
        random.NextBytes(pixels);

        using var ms = new MemoryStream();
        var options = subsampling.HasValue
            ? new JpegEncoderOptions { Quality = 90, Subsampling = subsampling.Value }
            : new JpegEncoderOptions { Quality = 90 };

        JpegEncoder.Encode(source, ms, options);
        return ms.ToArray();
    }
}
