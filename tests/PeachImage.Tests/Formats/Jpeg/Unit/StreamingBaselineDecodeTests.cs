using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Decoding;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Tests.Formats.Jpeg.Unit;

/// <summary>
/// A baseline frame whose scan carries every component is decoded four MCU rows at a time, the inverse transform of one
/// batch overlapping the entropy decoding of the next. That has to produce exactly what keeping every coefficient and
/// transforming afterward does, whatever the image size, subsampling, restart interval or thread settings.
/// </summary>
public class StreamingBaselineDecodeTests
{
    // Heights chosen to end mid-batch (4 MCU rows = 32, 64 or 16 pixel rows depending on subsampling), to span several batches,
    // and to leave partial MCUs at the right and bottom edges.
    public static TheoryData<JpegChromaSubsampling, bool, int, int, int> Cases() => new()
    {
        { JpegChromaSubsampling.Yuv420, true, 200, 301, 0 },
        { JpegChromaSubsampling.Yuv420, true, 37, 97, 0 },
        { JpegChromaSubsampling.Yuv444, true, 64, 129, 0 },
        { JpegChromaSubsampling.Yuv422, true, 90, 71, 0 },
        { JpegChromaSubsampling.Yuv411, true, 130, 50, 0 },
        { JpegChromaSubsampling.Yuv420, false, 150, 203, 0 },
        { JpegChromaSubsampling.Yuv420, true, 200, 301, 7 },
        { JpegChromaSubsampling.Yuv444, true, 64, 129, 3 },
        { JpegChromaSubsampling.Yuv420, false, 41, 65, 5 },
        { JpegChromaSubsampling.Yuv420, true, 16, 16, 0 },
        { JpegChromaSubsampling.Yuv420, true, 1, 1, 0 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void StreamedFrame_ReconstructsToExactlyTheSamePixels_AsTheFullCoefficientPath(JpegChromaSubsampling subsampling, bool color, int width, int height, int restartInterval)
    {
        byte[] jpeg = Encode(color, width, height, subsampling, restartInterval);

        foreach (var parallel in new[] { new ParallelSettings(1, null), default })
        {
            using var fullStream = new MemoryStream(jpeg);
            var full = FrameDecoder.Decode(fullStream);
            using var streamedStream = new MemoryStream(jpeg);
            var streamed = FrameDecoder.Decode(streamedStream, streamBaseline: parallel);

            try
            {
                Assert.Null(full.StreamedPlanes);
                Assert.NotNull(streamed.StreamedPlanes);

                using var expected = FrameReconstructor.Reconstruct(full, null);
                using var actual = FrameReconstructor.Reconstruct(streamed, null);

                Assert.Equal(expected.PixelFormat, actual.PixelFormat);
                Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
            }
            finally
            {
                full.ReleaseBuffers();
                streamed.ReleaseBuffers();
            }
        }
    }

    [Fact]
    public void ProgressiveFrame_IsNotStreamed()
    {
        byte[] jpeg = Encode(color: true, 64, 64, JpegChromaSubsampling.Yuv420, restartInterval: 0, progressive: true);

        using var stream = new MemoryStream(jpeg);
        var frame = FrameDecoder.Decode(stream, streamBaseline: default(ParallelSettings));
        try
        {
            Assert.Null(frame.StreamedPlanes);
        }
        finally
        {
            frame.ReleaseBuffers();
        }
    }

    [Fact]
    public void TruncatedStreamedScan_FailsCleanlyOrDecodesWhatItHas_AndLeavesNothingBehind()
    {
        byte[] jpeg = Encode(color: true, 200, 301, JpegChromaSubsampling.Yuv420, restartInterval: 0);

        // Cut at several points inside the entropy-coded data: each must either throw a JpegDecodingException or decode;
        // none may throw anything else (a transform still running behind a failed decode is waited for before cleanup).
        for (int cut = jpeg.Length / 4; cut < jpeg.Length; cut += jpeg.Length / 7)
        {
            using var stream = new MemoryStream(jpeg, 0, cut);
            try
            {
                using var image = JpegDecoder.Decode(stream);
                Assert.Equal(200, image.Width);
            }
            catch (JpegDecodingException)
            {
            }
        }
    }

    [Fact]
    public void FastUpsampling_IgnoresTheRowsConvertedWithTheDefaultFilter()
    {
        byte[] jpeg = Encode(color: true, 130, 77, JpegChromaSubsampling.Yuv420, restartInterval: 0);
        var options = new JpegDecoderOptions { FastUpsampling = true };

        using var fullStream = new MemoryStream(jpeg);
        var full = FrameDecoder.Decode(fullStream);
        using var streamedStream = new MemoryStream(jpeg);
        var streamed = FrameDecoder.Decode(streamedStream, streamBaseline: new ParallelSettings(1, null));
        try
        {
            using var expected = FrameReconstructor.Reconstruct(full, options);
            using var actual = FrameReconstructor.Reconstruct(streamed, options);
            Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
        }
        finally
        {
            full.ReleaseBuffers();
            streamed.ReleaseBuffers();
        }
    }

    private static byte[] Encode(bool color, int width, int height, JpegChromaSubsampling subsampling, int restartInterval, bool progressive = false)
    {
        var random = new Random(width * 31 + height);
        int channels = color ? 3 : 1;
        byte[] pixels = new byte[width * height * channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    pixels[(((y * width) + x) * channels) + c] = (byte)Math.Clamp(((x * 3) + (y * 2) + (c * 40) + random.Next(-25, 26)) % 256, 0, 255);
                }
            }
        }

        using var image = Image.Create(width, height, color ? PixelFormat.Rgb24 : PixelFormat.Gray8);
        pixels.CopyTo(image.GetPixelSpan());
        using var output = new MemoryStream();
        image.Save(output, "jpeg", new JpegEncoderOptions { Quality = 85, Subsampling = subsampling, RestartInterval = restartInterval, Progressive = progressive });
        return output.ToArray();
    }
}
