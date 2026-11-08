using System.Buffers;
using PeachImage.Formats.Jpeg.Dct;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Internal;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Decodes a baseline scan that carries every component a few MCU rows at a time: each batch of rows is entropy-
/// decoded into a small coefficient buffer and inverse-transformed straight into the component planes while the
/// next batch is being decoded, so the frame's coefficients (three bytes a pixel, written, then read back) never
/// exist at full size. Two batch buffers alternate; the transform of one batch runs on another thread while the
/// scan decoder (which cannot be split up) works on the next.
/// </summary>
internal static class StreamingBaselineScan
{
    /// <summary>MCU rows per batch: small enough that a batch of coefficients stays in cache, large enough that handing it off is cheap.</summary>
    private const int BatchMcuRows = 4;

    /// <summary>
    /// Decodes the scan and returns each frame component's inverse-transformed plane (pooled; the caller owns them), in the
    /// order of <paramref name="frameComponents"/>. Every frame component must be in <paramref name="scanComponents"/>.
    /// When <paramref name="colorSpace"/> is one the row converter handles, the image rows are also upsampled and converted
    /// behind the transforms, and <paramref name="output"/> returns the finished bytes (otherwise it is <see langword="null"/>).
    /// </summary>
    public static byte[][] Decode(
        JpegEntropyReader entropy,
        ComponentDecodeState[] frameComponents,
        ComponentDecodeState[] scanComponents,
        HuffmanDecodingTable[] dcTables,
        HuffmanDecodingTable[] acTables,
        RestartController restart,
        int mcusAcross,
        int mcusDown,
        ParallelSettings parallel,
        JpegColorSpace colorSpace,
        int imageWidth,
        int imageHeight,
        out byte[]? output,
        CallerOutput? target = null)
    {
        output = null;
        int componentCount = frameComponents.Length;
        var planes = new byte[componentCount][];
        var dequant = new float[componentCount][];
        var buffers = new JpegCoefficientBuffer?[2][] { new JpegCoefficientBuffer?[componentCount], new JpegCoefficientBuffer?[componentCount] };

        bool grayIsOutput = target is null && colorSpace == JpegColorSpace.Grayscale && frameComponents.Length == 1
            && frameComponents[0].ActualWidthInSamples == imageWidth && frameComponents[0].ActualHeightInSamples == imageHeight;

        // Work behind the decode runs on its own threads only when that cannot break the caller's limits: the decoding thread plus
        // a transform task plus a conversion task is three threads, so a lower cap, or a scheduler of the caller's own (which
        // may be bounded or may not run a task until asked), keeps everything on the calling thread.
        // A grayscale frame's conversion is a plain row copy: one pass after the last transform costs less than a task per batch.
        bool deferConversion = colorSpace == JpegColorSpace.Grayscale;
        bool overlap = parallel.Scheduler is null && parallel.EffectiveDegree >= 3;
        StreamingRowConverter? converter = null;
        BatchWorker? worker = null;
        byte[]? convertedBuffer = null;
        bool succeeded = false;

        try
        {
            for (int i = 0; i < componentCount; i++)
            {
                var component = frameComponents[i];
                if (grayIsOutput)
                {
                    // A grayscale plane of the image's own size, row for row, is already the finished image.
                    planes[i] = ImageBufferPool.Shared.Rent(imageWidth * imageHeight);
                }
                else
                {
                    planes[i] = ArrayPool<byte>.Shared.Rent(component.ActualWidthInSamples * component.ActualHeightInSamples);
                }

                dequant[i] = DequantTableCache.Get(component.QuantizationTable.Values);

                int blocksWide = mcusAcross * component.BlocksPerMcuHorizontal;
                int rows = component.BlocksPerMcuVertical * BatchMcuRows;
                buffers[0][i] = new JpegCoefficientBuffer(blocksWide, rows);
                buffers[1][i] = new JpegCoefficientBuffer(blocksWide, rows);
            }

            // Rows are converted behind the transforms when the frame's shape allows it.
            if (!grayIsOutput && colorSpace is JpegColorSpace.YCbCr or JpegColorSpace.Grayscale)
            {
                if (target is not null)
                {
                    // The caller's memory takes the rows directly; nothing is rented for the output.
                    converter = StreamingRowConverter.TryCreate(frameComponents, planes, colorSpace, imageWidth, imageHeight, null, target);
                }
                else
                {
                    int bytesPerPixel = colorSpace == JpegColorSpace.Grayscale ? 1 : 3;
                    convertedBuffer = ImageBufferPool.Shared.Rent(checked(imageWidth * imageHeight * bytesPerPixel));
                    converter = StreamingRowConverter.TryCreate(frameComponents, planes, colorSpace, imageWidth, imageHeight, convertedBuffer);
                    if (converter is null)
                    {
                        ImageBufferPool.Shared.Return(convertedBuffer);
                        convertedBuffer = null;
                    }
                }
            }

            if (overlap)
            {
                worker = new BatchWorker(frameComponents, buffers, dequant, planes, deferConversion ? null : converter);
            }

            BaselineScanDecoder.BeginScan(scanComponents);

            int batch = 0;
            for (int mcuY = 0; mcuY < mcusDown; mcuY += BatchMcuRows, batch++)
            {
                int slot = batch & 1;

                // The buffers of this slot were last used two batches ago: wait for their transform, then reuse them.
                worker?.WaitForTransforms(batch - 1);
                var slotBuffers = buffers[slot];
                for (int i = 0; i < componentCount; i++)
                {
                    var buffer = slotBuffers[i]!;
                    if (batch >= 2)
                    {
                        buffer.Clear();
                    }

                    frameComponents[i].Coefficients = buffer;
                }

                int mcuEnd = Math.Min(mcuY + BatchMcuRows, mcusDown);
                BaselineScanDecoder.DecodeBatch(entropy, scanComponents, dcTables, acTables, restart, mcusAcross, mcuY, mcuEnd);

                int mcuRowCount = mcuEnd - mcuY;

                if (worker is null)
                {
                    TransformBatch(frameComponents, slotBuffers, dequant, planes, mcuY, mcuRowCount);
                    if (!deferConversion)
                    {
                        converter?.ConvertReady(mcuEnd, parallel);
                    }
                }
                else
                {
                    worker.Enqueue(slot, mcuY, mcuRowCount, mcuEnd);
                }
            }

            worker?.Finish();
            if (deferConversion)
            {
                converter?.ConvertReady(mcusDown, parallel);
            }

            if (target is not null)
            {
                target.Written = converter is not null;
            }

            output = grayIsOutput ? planes[0] : convertedBuffer;
            convertedBuffer = null;
            succeeded = true;
            return planes;
        }
        finally
        {
            if (!succeeded)
            {
                // A transform still running reads these buffers and writes these planes: let it finish before they go back.
                worker?.Abort();

                if (convertedBuffer is not null)
                {
                    ImageBufferPool.Shared.Return(convertedBuffer);
                }

                foreach (var plane in planes)
                {
                    if (plane is null)
                    {
                        continue;
                    }

                    if (grayIsOutput)
                    {
                        ImageBufferPool.Shared.Return(plane);
                    }
                    else
                    {
                        ArrayPool<byte>.Shared.Return(plane);
                    }
                }
            }

            // Every task that could still use the converter has finished by now, on both the success and the failure path.
            converter?.Release();

            foreach (var slotBuffers in buffers)
            {
                foreach (var buffer in slotBuffers)
                {
                    buffer?.Return();
                }
            }
        }
    }

    /// <summary>
    /// Runs the inverse transforms of batches, and the conversion of their rows, on the thread pool while the calling thread
    /// entropy-decodes the next batches. Each batch's transform is queued as soon as it is decoded; conversion follows behind, one batch
    /// after another, once every transform up to that batch has finished. It replaces a task and a continuation per batch with
    /// preallocated pool work items and a small ring of batch descriptors, so the only allocations are the worker and its few items.
    /// </summary>
    private sealed class BatchWorker
    {
        private const int RingSize = 4;

        private readonly object _gate = new();
        private readonly (int Slot, int FirstMcuRow, int McuRowCount, int McuRowsDone)[] _ring = new (int, int, int, int)[RingSize];
        private readonly bool[] _transformDone = new bool[RingSize];
        private readonly TransformWork[] _transformItems = new TransformWork[RingSize];
        private readonly ConvertWork _convertItem;
        private readonly ComponentDecodeState[] _components;
        private readonly JpegCoefficientBuffer?[][] _buffers;
        private readonly float[][] _dequant;
        private readonly byte[][] _planes;
        private readonly StreamingRowConverter? _converter;

        // Batches enqueued, the leading run of batches whose transforms have all finished (transforms of different batches may finish in
        // any order), and batches converted. A ring entry is free once its batch is converted (with no converter, once it is part of the
        // finished run). `_pending` counts queued items that have not finished.
        private int _tail;
        private int _transformed;
        private int _converted;
        private int _pending;
        private bool _convertQueued;
        private Exception? _error;

        public BatchWorker(
            ComponentDecodeState[] components,
            JpegCoefficientBuffer?[][] buffers,
            float[][] dequant,
            byte[][] planes,
            StreamingRowConverter? converter)
        {
            _components = components;
            _buffers = buffers;
            _dequant = dequant;
            _planes = planes;
            _converter = converter;

            for (int i = 0; i < RingSize; i++)
            {
                _transformItems[i] = new TransformWork(this, i);
            }

            _convertItem = new ConvertWork(this);
        }

        /// <summary>Hands a decoded batch over; its transform is queued at once.</summary>
        public void Enqueue(int slot, int firstMcuRow, int mcuRowCount, int mcuRowsDone)
        {
            int index;
            lock (_gate)
            {
                ThrowIfFailed();
                while (_tail - FreedUpTo == RingSize)
                {
                    Monitor.Wait(_gate);
                    ThrowIfFailed();
                }

                index = _tail % RingSize;
                _ring[index] = (slot, firstMcuRow, mcuRowCount, mcuRowsDone);
                _transformDone[index] = false;
                _tail++;
                _pending++;
            }

            ThreadPool.UnsafeQueueUserWorkItem(_transformItems[index], preferLocal: false);
        }

        /// <summary>Blocks until at least <paramref name="count"/> batches have been inverse-transformed (so their coefficient buffers are free again).</summary>
        public void WaitForTransforms(int count)
        {
            if (count <= 0)
            {
                return;
            }

            lock (_gate)
            {
                while (_transformed < count && _error is null)
                {
                    Monitor.Wait(_gate);
                }

                ThrowIfFailed();
            }
        }

        /// <summary>Waits for every enqueued batch to be transformed and converted, and rethrows a failure of either step.</summary>
        public void Finish()
        {
            lock (_gate)
            {
                while (_pending > 0 || FreedUpTo < _tail)
                {
                    if (_error is not null && _pending == 0)
                    {
                        break;
                    }

                    Monitor.Wait(_gate);
                }

                ThrowIfFailed();
            }
        }

        /// <summary>Waits for queued work to stop touching the decode's buffers without reporting a failure: the decode's own failure is the one being propagated.</summary>
        public void Abort()
        {
            lock (_gate)
            {
                _error ??= new OperationCanceledException();
                while (_pending > 0)
                {
                    Monitor.Wait(_gate);
                }
            }
        }

        /// <summary>How many batches no longer need their ring entry: converted ones, or with no converter, transformed ones.</summary>
        private int FreedUpTo => _converter is null ? _transformed : _converted;

        private void ThrowIfFailed()
        {
            if (_error is { } error && error is not OperationCanceledException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        private void RunTransform(int index)
        {
            (int Slot, int FirstMcuRow, int McuRowCount, int McuRowsDone) batch;
            lock (_gate)
            {
                batch = _ring[index];
            }

            Exception? failure = null;
            if (_error is null)
            {
                try
                {
                    TransformBatch(_components, _buffers[batch.Slot], _dequant, _planes, batch.FirstMcuRow, batch.McuRowCount);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            bool queueConvert = false;
            lock (_gate)
            {
                if (failure is not null)
                {
                    _error ??= failure;
                }

                _transformDone[index] = true;
                while (_transformed < _tail && _transformDone[_transformed % RingSize])
                {
                    _transformed++;
                }

                if (_converter is not null && !_convertQueued && _converted < _transformed && _error is null)
                {
                    _convertQueued = true;
                    _pending++;
                    queueConvert = true;
                }

                _pending--;
                Monitor.PulseAll(_gate);
            }

            if (queueConvert)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_convertItem, preferLocal: false);
            }
        }

        private void RunConvert()
        {
            while (true)
            {
                int mcuRowsDone;
                lock (_gate)
                {
                    if (_error is not null || _converted >= _transformed)
                    {
                        _convertQueued = false;
                        _pending--;
                        Monitor.PulseAll(_gate);
                        return;
                    }

                    mcuRowsDone = _ring[_converted % RingSize].McuRowsDone;
                }

                try
                {
                    // Conversions run one after another, since each continues where the last stopped; the batch is only taken once its
                    // transform and every earlier one are done.
                    _converter!.ConvertReady(mcuRowsDone, new ParallelSettings(1, null));
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        _error ??= exception;
                        _convertQueued = false;
                        _pending--;
                        Monitor.PulseAll(_gate);
                    }

                    return;
                }

                lock (_gate)
                {
                    _converted++;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private sealed class TransformWork(BatchWorker owner, int index) : IThreadPoolWorkItem
        {
            public void Execute() => owner.RunTransform(index);
        }

        private sealed class ConvertWork(BatchWorker owner) : IThreadPoolWorkItem
        {
            public void Execute() => owner.RunConvert();
        }
    }

    private static void TransformBatch(
        ComponentDecodeState[] components,
        JpegCoefficientBuffer?[] buffers,
        float[][] dequant,
        byte[][] planes,
        int firstMcuRow,
        int mcuRowCount)
    {
        for (int i = 0; i < components.Length; i++)
        {
            var component = components[i];
            int blockRows = mcuRowCount * component.BlocksPerMcuVertical;
            FrameReconstructor.InverseTransformBlockRows(
                buffers[i]!,
                firstMcuRow * component.BlocksPerMcuVertical,
                blockRows,
                dequant[i],
                planes[i],
                component.ActualWidthInSamples,
                component.ActualHeightInSamples,
                blockSize: 8,
                scaleDenominator: 1,
                parallel: new ParallelSettings(1, null));
        }
    }
}
