namespace PeachImage.Formats.Shared.Parallelism;

/// <summary>
/// The per-call parallelism a caller asked for, carried from the public options down to <see cref="RowParallel"/>.
/// <c>default</c> is the library's historical behaviour: fan out over up to <see cref="Environment.ProcessorCount"/> workers
/// on the shared thread pool.
/// </summary>
internal readonly struct ParallelSettings
{
    private readonly int _maxDegreeOfParallelism;

    public ParallelSettings(int maxDegreeOfParallelism, TaskScheduler? scheduler)
    {
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        Scheduler = scheduler;
    }

    /// <summary>The requested degree of parallelism; zero or negative means "the default cap".</summary>
    public int MaxDegreeOfParallelism => _maxDegreeOfParallelism;

    public TaskScheduler? Scheduler { get; }

    /// <summary>True when everything must run on the calling thread.</summary>
    public bool IsSequential => _maxDegreeOfParallelism == 1;

    public int EffectiveDegree => _maxDegreeOfParallelism > 0 ? _maxDegreeOfParallelism : Environment.ProcessorCount;

    public ParallelOptions ToParallelOptions()
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = EffectiveDegree };
        if (Scheduler is not null)
        {
            options.TaskScheduler = Scheduler;
        }

        return options;
    }

    public static ParallelSettings From(DecoderOptions? options) =>
        options is null ? default : new ParallelSettings(options.MaxDegreeOfParallelism, options.TaskScheduler);

    public static ParallelSettings From(ResizeOptions? options) =>
        options is null ? default : new ParallelSettings(options.MaxDegreeOfParallelism, options.TaskScheduler);

    public static ParallelSettings From(EncoderOptions? options) =>
        options is null ? default : new ParallelSettings(options.MaxDegreeOfParallelism, options.TaskScheduler);
}
