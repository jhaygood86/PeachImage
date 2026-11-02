using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Encoder.Jpeg;
using PeachImage.Formats.Jxl.Jpeg;
using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Tests.Formats.Jxl.Unit;

namespace PeachImage.Tests.Parallelism;

/// <summary>Parallelism limits on the AVIF reconstruction pipeline thread and the JPEG XL JPEG-transcode paths.</summary>
public class RemainingPathsParallelismTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private static byte[] EncodeLossyAvif()
    {
        // 320x288 in one tile is 5760 4x4 units, past the 4096 the pipelined reconstruction needs.
        using var image = JxlJpegTestImages.Create(320, 288, gray: false);
        using var stream = new MemoryStream();
        image.Save(stream, "avif", new AvifEncoderOptions { Quality = 60 });
        return stream.ToArray();
    }

    private static Image DecodeWithTimeout(byte[] avif, DecoderOptions? options)
    {
        var task = Task.Run(() => Image.Load(avif, options));
        Assert.True(task.Wait(Timeout), "decode did not complete (deadlock?)");
        return task.Result;
    }

    [Fact]
    public void AvifDecode_WithDegreeOne_MatchesTheDefault()
    {
        byte[] avif = EncodeLossyAvif();
        using var expected = DecodeWithTimeout(avif, null);
        using var actual = DecodeWithTimeout(avif, new DecoderOptions { MaxDegreeOfParallelism = 1 });
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
    }

    [Fact]
    public void AvifDecode_WithSingleSlotScheduler_CompletesAndMatchesTheDefault()
    {
        byte[] avif = EncodeLossyAvif();
        using var expected = DecodeWithTimeout(avif, null);
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ConcurrentScheduler;
        using var actual = DecodeWithTimeout(avif, new DecoderOptions { TaskScheduler = scheduler });
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
    }

    [Fact]
    public void AvifDecode_WithTwoSlotScheduler_CompletesAndMatchesTheDefault()
    {
        byte[] avif = EncodeLossyAvif();
        using var expected = DecodeWithTimeout(avif, null);
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2).ConcurrentScheduler;
        using var actual = DecodeWithTimeout(avif, new DecoderOptions { TaskScheduler = scheduler });
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
    }

    [Fact]
    public void ReconPipeline_OnASchedulerThatNeverRunsTasks_FallsBackToInlineExecution()
    {
        var worker = new CountingWorker();
        var started = DateTime.UtcNow;
        using var pipeline = new Av1ReconPipeline(worker, threaded: true, new StarvedScheduler());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.False(pipeline.Threaded);

        var command = default(Av1ReconCommand);
        for (int i = 0; i < 2000; i++)
        {
            pipeline.Submit(ref command, new int[4], 0);
        }

        pipeline.Drain();
        pipeline.Finish();
        Assert.Equal(2000, worker.Executed);
    }

    [Fact]
    public void ReconPipeline_OnASingleSlotScheduler_RunsInline()
    {
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ConcurrentScheduler;
        using var pipeline = new Av1ReconPipeline(new CountingWorker(), threaded: true, scheduler);
        Assert.False(pipeline.Threaded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JxlJpegTranscode_WithDegreeOne_MatchesTheDefault(bool gray)
    {
        byte[] jpeg = JxlJpegTestImages.Encode(700, 530, gray, new JpegEncoderOptions { Quality = 85, Subsampling = JpegChromaSubsampling.Yuv420 });
        var sequential = new ParallelSettings(1, null);

        byte[] expected = JxlJpegRecompressor.Recompress(jpeg);
        byte[] actual = JxlJpegRecompressor.Recompress(jpeg, sequential);
        Assert.Equal(expected, actual);

        Assert.Equal(jpeg, JxlJpegReconstructor.Reconstruct(expected));
        Assert.Equal(jpeg, JxlJpegReconstructor.Reconstruct(expected, sequential));
        Assert.Equal(jpeg, JxlJpegReconstructor.Reconstruct(expected, new ParallelSettings(2, new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2).ConcurrentScheduler)));
    }

    [Fact]
    public void JxlDecodeFrames_WithDegreeOne_MatchesTheDefault()
    {
        byte[] jpeg = JxlJpegTestImages.Encode(700, 530, false, new JpegEncoderOptions { Quality = 85 });
        byte[] jxl = JxlJpegRecompression.RecompressJpeg(new MemoryStream(jpeg));

        var expected = JxlDecoder.DecodeFrames(new MemoryStream(jxl), PixelFormat.Rgb24);
        var actual = JxlDecoder.DecodeFrames(new MemoryStream(jxl), PixelFormat.Rgb24, new ParallelSettings(1, null));
        try
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.True(expected[i].GetPixelSpan().SequenceEqual(actual[i].GetPixelSpan()));
            }
        }
        finally
        {
            expected.ForEach(i => i.Dispose());
            actual.ForEach(i => i.Dispose());
        }
    }

    private sealed class CountingWorker : IAv1ReconWorker
    {
        public int Executed;

        public void Execute(in Av1ReconCommand command, ReadOnlySpan<int> quant) => Executed++;

        public void PredictIntrabc(int plane, int startX, int startY, int w, int h, int mvRow, int mvCol, int subX, int subY, int lastX, int lastY) { }

        public void WritePalette(int plane, int startX, int startY, int w, int h, int[] palette, int[] colorMap, int mapWidth, int localBaseX, int localBaseY) { }
    }

    /// <summary>Claims plenty of concurrency but never runs anything, like a saturated shared scheduler.</summary>
    private sealed class StarvedScheduler : TaskScheduler
    {
        public override int MaximumConcurrencyLevel => 8;

        protected override void QueueTask(Task task) { }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
