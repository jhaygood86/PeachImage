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
        out byte[]? output)
    {
        output = null;
        int componentCount = frameComponents.Length;
        var planes = new byte[componentCount][];
        var dequant = new float[componentCount][];
        var buffers = new JpegCoefficientBuffer?[2][] { new JpegCoefficientBuffer?[componentCount], new JpegCoefficientBuffer?[componentCount] };
        var pending = new Task?[2];

        bool grayIsOutput = colorSpace == JpegColorSpace.Grayscale && frameComponents.Length == 1
            && frameComponents[0].ActualWidthInSamples == imageWidth && frameComponents[0].ActualHeightInSamples == imageHeight;

        // Work behind the decode runs on its own threads only when that cannot break the caller's limits: the decoding thread plus
        // a transform task plus a conversion task is three threads, so a lower cap, or a scheduler of the caller's own (which
        // may be bounded or may not run a task until asked), keeps everything on the calling thread.
        bool overlap = parallel.Scheduler is null && parallel.EffectiveDegree >= 3;
        Task? lastConvert = null;
        StreamingRowConverter? converter = null;
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

                dequant[i] = DctKernelSelector.Inverse.PrepareDequantTable(component.QuantizationTable.Values);

                int blocksWide = mcusAcross * component.BlocksPerMcuHorizontal;
                int rows = component.BlocksPerMcuVertical * BatchMcuRows;
                buffers[0][i] = new JpegCoefficientBuffer(blocksWide, rows);
                buffers[1][i] = new JpegCoefficientBuffer(blocksWide, rows);
            }

            // Rows are converted behind the transforms when the frame's shape allows it.
            if (!grayIsOutput && colorSpace is JpegColorSpace.YCbCr or JpegColorSpace.Grayscale)
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

            BaselineScanDecoder.BeginScan(scanComponents);

            int batch = 0;
            for (int mcuY = 0; mcuY < mcusDown; mcuY += BatchMcuRows, batch++)
            {
                int slot = batch & 1;

                // The buffers of this slot were last used two batches ago: wait for their transform, then reuse them.
                Complete(ref pending[slot]);
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

                int firstMcuRow = mcuY;
                int mcuRowCount = mcuEnd - mcuY;
                int mcuRowsDone = mcuEnd;
                void Transform() => TransformBatch(frameComponents, slotBuffers, dequant, planes, firstMcuRow, mcuRowCount);

                if (!overlap)
                {
                    Transform();
                    converter?.ConvertReady(mcuRowsDone, parallel);
                }
                else
                {
                    var scheduler = TaskScheduler.Default;
                    var transformTask = Task.Factory.StartNew(Transform, CancellationToken.None, TaskCreationOptions.None, scheduler);
                    pending[slot] = transformTask;

                    if (converter is not null)
                    {
                        // Converting rows needs every earlier transform finished (the previous conversion task stands for all of
                        // them) and this one; conversions also run one after another, since each continues where the last stopped.
                        var conversionConverter = converter;
                        Task[] prerequisites = lastConvert is null ? [transformTask] : [transformTask, lastConvert];
                        lastConvert = Task.Factory.ContinueWhenAll(
                            prerequisites,
                            _ => conversionConverter.ConvertReady(mcuRowsDone, new ParallelSettings(1, null)),
                            CancellationToken.None,
                            TaskContinuationOptions.None,
                            scheduler);
                    }
                }
            }

            Complete(ref pending[0]);
            Complete(ref pending[1]);
            Complete(ref lastConvert);
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
                foreach (var task in new[] { pending[0], pending[1], lastConvert })
                {
                    try
                    {
                        task?.GetAwaiter().GetResult();
                    }
                    catch (Exception)
                    {
                        // The failure being propagated is the decode's own; a transform failure behind it adds nothing.
                    }
                }

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

            foreach (var slotBuffers in buffers)
            {
                foreach (var buffer in slotBuffers)
                {
                    buffer?.Return();
                }
            }
        }
    }

    private static void Complete(ref Task? task)
    {
        task?.GetAwaiter().GetResult();
        task = null;
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
