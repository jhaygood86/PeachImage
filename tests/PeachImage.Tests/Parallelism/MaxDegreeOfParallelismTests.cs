using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Parallelism;

/// <summary>
/// <c>MaxDegreeOfParallelism</c> and <c>TaskScheduler</c> on the decoder, resize and conversion paths. A recording scheduler observes
/// which work reaches the thread pool: with a degree of 1 nothing may be scheduled (the call stays on the calling thread), and the output
/// must be byte-identical to the default. Images are well past <c>RowParallel.MinRowsForParallel</c> (64 rows) so the parallel path engages.
/// </summary>
public class MaxDegreeOfParallelismTests
{
    [Fact]
    public void JpegDecode_WithDegreeOne_RunsOnTheCallingThreadOnly_AndMatchesTheDefault()
    {
        byte[] jpeg = EncodeNoisyJpeg(645, 517);

        using var expected = Image.Load(jpeg);

        var scheduler = new RecordingScheduler();
        using var sequential = Image.Load(jpeg, new DecoderOptions { MaxDegreeOfParallelism = 1, TaskScheduler = scheduler });

        Assert.Equal(0, scheduler.TasksRun);
        Assert.True(expected.GetPixelSpan().SequenceEqual(sequential.GetPixelSpan()));
    }

    [Fact]
    public void JpegDecode_WithDefaultDegree_UsesTheSuppliedScheduler()
    {
        byte[] jpeg = EncodeNoisyJpeg(645, 517);

        var scheduler = new RecordingScheduler();
        using var image = Image.Load(jpeg, new DecoderOptions { MaxDegreeOfParallelism = 4, TaskScheduler = scheduler });

        Assert.True(scheduler.TasksRun > 0, "the parallel work should have gone through the supplied scheduler");
        using var expected = Image.Load(jpeg);
        Assert.True(expected.GetPixelSpan().SequenceEqual(image.GetPixelSpan()));
    }

    [Fact]
    public void JpegDecode_WithBoundedScheduler_NeverExceedsItsConcurrency()
    {
        byte[] jpeg = EncodeNoisyJpeg(645, 517);
        var scheduler = new RecordingScheduler(maximumConcurrency: 2);

        using var image = Image.Load(jpeg, new DecoderOptions { TaskScheduler = scheduler });

        Assert.True(scheduler.TasksRun > 0);
        Assert.True(scheduler.MaxConcurrent <= 2, $"observed {scheduler.MaxConcurrent} concurrent tasks");
    }

    [Theory]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Gray8)]
    public void Resize_WithDegreeOne_RunsOnTheCallingThreadOnly_AndMatchesTheDefault(PixelFormat format)
    {
        using var source = Image.Create(300, 260, format);
        new Random(7).NextBytes(source.GetPixelSpan());

        using var expected = source.Resize(97, 83);

        var scheduler = new RecordingScheduler();
        using var sequential = source.Resize(97, 83, new ResizeOptions { MaxDegreeOfParallelism = 1, TaskScheduler = scheduler });

        Assert.Equal(0, scheduler.TasksRun);
        Assert.True(expected.GetPixelSpan().SequenceEqual(sequential.GetPixelSpan()));
    }

    [Fact]
    public void Resize_WithBoundedScheduler_UsesIt()
    {
        using var source = Image.Create(300, 260, PixelFormat.Rgb24);
        new Random(7).NextBytes(source.GetPixelSpan());

        var scheduler = new RecordingScheduler(maximumConcurrency: 2);
        using var resized = source.Resize(97, 83, new ResizeOptions { TaskScheduler = scheduler });

        Assert.True(scheduler.TasksRun > 0);
        Assert.True(scheduler.MaxConcurrent <= 2);
    }

    [Fact]
    public void ConvertTo_WithDegreeOne_MatchesTheDefault_AndSchedulesNothing()
    {
        using var source = Image.Create(120, 200, PixelFormat.Rgb48);
        new Random(3).NextBytes(source.GetPixelSpan());

        using var expected = source.ConvertTo(PixelFormat.Rgba32);
        var scheduler = new RecordingScheduler();
        using var sequential = source.ConvertTo(PixelFormat.Rgba32, 1, scheduler);

        Assert.Equal(0, scheduler.TasksRun);
        Assert.True(expected.GetPixelSpan().SequenceEqual(sequential.GetPixelSpan()));
    }

    private static byte[] EncodeNoisyJpeg(int width, int height)
    {
        using var source = Image.Create(width, height, PixelFormat.Rgb24);
        new Random(12345).NextBytes(source.GetPixelSpan());
        using var ms = new MemoryStream();
        JpegEncoder.Encode(source, ms, new JpegEncoderOptions { Quality = 90, Subsampling = JpegChromaSubsampling.Yuv420 });
        return ms.ToArray();
    }

    /// <summary>Runs queued tasks on the thread pool, counting them and the most that ran at once.</summary>
    private sealed class RecordingScheduler(int maximumConcurrency = int.MaxValue) : TaskScheduler
    {
        private int _tasksRun;
        private int _running;
        private int _maxConcurrent;

        public int TasksRun => Volatile.Read(ref _tasksRun);

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public override int MaximumConcurrencyLevel => maximumConcurrency;

        protected override void QueueTask(Task task) =>
            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    Interlocked.Increment(ref _tasksRun);
                    int now = Interlocked.Increment(ref _running);
                    int seen;
                    while (now > (seen = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen)
                    {
                    }

                    try
                    {
                        TryExecuteTask(task);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _running);
                    }
                },
                null);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
