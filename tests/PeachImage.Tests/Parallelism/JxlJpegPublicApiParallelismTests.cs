using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jxl;

namespace PeachImage.Tests.Parallelism;

/// <summary>The additive <c>maxDegreeOfParallelism</c> / <c>taskScheduler</c> overloads of the JPEG XL JPEG recompression and reconstruction APIs.</summary>
public class JxlJpegPublicApiParallelismTests
{
    [Fact]
    public void RecompressAndReconstruct_WithBoundedParallelism_MatchTheDefaults()
    {
        byte[] jpeg = EncodeJpeg(700, 530);

        byte[] defaultJxl = JxlJpegRecompression.RecompressJpeg(new MemoryStream(jpeg));
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2).ConcurrentScheduler;

        Assert.Equal(defaultJxl, JxlJpegRecompression.RecompressJpeg(new MemoryStream(jpeg), 1));
        Assert.Equal(defaultJxl, JxlJpegRecompression.RecompressJpeg(new MemoryStream(jpeg), 4, scheduler));

        using var sink = new MemoryStream();
        JxlJpegRecompression.RecompressJpeg(new MemoryStream(jpeg), sink, 1);
        Assert.Equal(defaultJxl, sink.ToArray());

        Assert.True(JxlJpegRecompression.TryRecompressJpeg(new MemoryStream(jpeg), 1, null, out byte[]? tried));
        Assert.Equal(defaultJxl, tried);

        Assert.Equal(jpeg, JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(defaultJxl), 1));
        Assert.Equal(jpeg, JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(defaultJxl), 4, scheduler));

        using var jpegSink = new MemoryStream();
        JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(defaultJxl), jpegSink, 1);
        Assert.Equal(jpeg, jpegSink.ToArray());

        Assert.True(JxlJpegReconstruction.TryReconstructJpeg(new MemoryStream(defaultJxl), 1, null, out byte[]? rebuilt));
        Assert.Equal(jpeg, rebuilt);
    }

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var source = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = source.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 31) ^ (i >> 5));
        }

        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 85 });
        return ms.ToArray();
    }
}
