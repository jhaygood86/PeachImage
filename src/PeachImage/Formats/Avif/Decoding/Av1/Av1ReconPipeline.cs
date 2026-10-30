using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// Feeds <see cref="Av1ReconCommand"/>s from a tile's parse loop to an <see cref="IAv1ReconWorker"/>. Entropy
/// decoding never reads reconstructed pixels, and reconstruction never touches parser state, so on large
/// tiles the two halves run on separate threads: the parser batches commands (with a private copy of each
/// block's quantized coefficients) and a dedicated thread executes the batches in order. The arithmetic is
/// unchanged and every pixel is still written in the original order, so output is bit-identical to running
/// each command inline, which is what small tiles (and single-core machines) do.
/// </summary>
internal sealed class Av1ReconPipeline : IDisposable
{
    private const int CommandsPerBatch = 256;
    private const int CoefsPerBatch = 16384;
    private const int MaxQueuedBatches = 8;

    private readonly IAv1ReconWorker _worker;
    private readonly bool _threaded;

    private BlockingCollection<Batch>? _queue;
    private ConcurrentQueue<Batch>? _free;
    private Task? _consumer;
    private Batch? _current;
    private Batch? _marker;
    private ManualResetEventSlim? _drained;
    private ExceptionDispatchInfo? _error;

    public Av1ReconPipeline(IAv1ReconWorker worker, bool threaded)
    {
        _worker = worker;
        _threaded = threaded;

        if (threaded)
        {
            _queue = new BlockingCollection<Batch>(MaxQueuedBatches);
            _free = new ConcurrentQueue<Batch>();
            _marker = new Batch();
            _drained = new ManualResetEventSlim(false);
            _current = new Batch();
            _consumer = Task.Factory.StartNew(Consume, TaskCreationOptions.LongRunning);
        }
    }

    /// <summary>Whether commands run on a separate thread (as opposed to inline in <see cref="Submit"/>).</summary>
    public bool Threaded => _threaded;

    /// <summary>
    /// Issues <paramref name="command"/>. <paramref name="quant"/> must hold the block's coefficients in its
    /// first <paramref name="coefCount"/> entries; inline execution reads them in place, threaded execution
    /// copies them, so the caller may reuse the array immediately either way.
    /// </summary>
    public void Submit(ref Av1ReconCommand command, int[] quant, int coefCount)
    {
        if (!_threaded)
        {
            command.CoefOffset = 0;
            _worker.Execute(command, quant);
            return;
        }

        var batch = _current!;
        if (batch.Count == CommandsPerBatch || batch.CoefUsed + coefCount > CoefsPerBatch)
        {
            Publish();
            batch = _current!;
        }

        command.CoefOffset = batch.CoefUsed;
        if (coefCount > 0)
        {
            quant.AsSpan(0, coefCount).CopyTo(batch.Coefs.AsSpan(batch.CoefUsed));
            batch.CoefUsed += coefCount;
        }

        batch.Commands[batch.Count++] = command;
    }

    /// <summary>Hands everything submitted so far to the worker thread (no-op for inline execution); call at natural boundaries so the worker is never starved.</summary>
    public void Flush()
    {
        if (_threaded && _current!.Count > 0)
        {
            Publish();
        }
    }

    /// <summary>Blocks until every submitted command has run, so the planes can be touched directly.</summary>
    public void Drain()
    {
        if (!_threaded)
        {
            return;
        }

        Flush();
        _drained!.Reset();
        _queue!.Add(_marker!);
        _drained.Wait();
        _error?.Throw();
    }

    /// <summary>Runs out the remaining work and stops the worker thread, rethrowing any failure it hit.</summary>
    public void Finish()
    {
        if (!_threaded)
        {
            return;
        }

        Flush();
        _queue!.CompleteAdding();
        _consumer!.Wait();
        _error?.Throw();
    }

    /// <summary>Stops the worker thread after a parse failure, discarding any error of its own; never throws.</summary>
    public void Abort()
    {
        if (!_threaded)
        {
            return;
        }

        _queue!.CompleteAdding();
        try
        {
            _consumer!.Wait();
        }
        catch (AggregateException)
        {
        }

    }

    /// <summary>Releases the queue and signal; call after <see cref="Finish"/> or <see cref="Abort"/> (the worker thread has stopped by then).</summary>
    public void Dispose()
    {
        _queue?.Dispose();
        _drained?.Dispose();
    }

    private void Publish()
    {
        var full = _current!;
        _queue!.Add(full);
        _current = _free!.TryDequeue(out var reused) ? reused : new Batch();
    }

    private void Consume()
    {
        foreach (var batch in _queue!.GetConsumingEnumerable())
        {
            if (ReferenceEquals(batch, _marker))
            {
                _drained!.Set();
                continue;
            }

            if (_error is null)
            {
                try
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        _worker.Execute(batch.Commands[i], batch.Coefs);
                    }
                }
                catch (Exception ex)
                {
                    _error = ExceptionDispatchInfo.Capture(ex);
                }
            }

            batch.Count = 0;
            batch.CoefUsed = 0;
            _free!.Enqueue(batch);
        }
    }

    private sealed class Batch
    {
        public readonly Av1ReconCommand[] Commands = new Av1ReconCommand[CommandsPerBatch];
        public readonly int[] Coefs = new int[CoefsPerBatch];
        public int Count;
        public int CoefUsed;
    }
}
