using System.Buffers;
using PeachImage.Formats.Jpeg.Dct;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Shared.Parallelism;

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
        ParallelSettings parallel)
    {
        int componentCount = frameComponents.Length;
        var planes = new byte[componentCount][];
        var dequant = new float[componentCount][];
        var buffers = new JpegCoefficientBuffer?[2][] { new JpegCoefficientBuffer?[componentCount], new JpegCoefficientBuffer?[componentCount] };
        var pending = new Task?[2];
        bool succeeded = false;

        try
        {
            for (int i = 0; i < componentCount; i++)
            {
                var component = frameComponents[i];
                planes[i] = ArrayPool<byte>.Shared.Rent(component.ActualWidthInSamples * component.ActualHeightInSamples);
                dequant[i] = DctKernelSelector.Inverse.PrepareDequantTable(component.QuantizationTable.Values);

                int blocksWide = mcusAcross * component.BlocksPerMcuHorizontal;
                int rows = component.BlocksPerMcuVertical * BatchMcuRows;
                buffers[0][i] = new JpegCoefficientBuffer(blocksWide, rows);
                buffers[1][i] = new JpegCoefficientBuffer(blocksWide, rows);
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
                void Transform() => TransformBatch(frameComponents, slotBuffers, dequant, planes, firstMcuRow, mcuRowCount);

                if (parallel.IsSequential)
                {
                    Transform();
                }
                else
                {
                    pending[slot] = Task.Factory.StartNew(Transform, CancellationToken.None, TaskCreationOptions.None, parallel.Scheduler ?? TaskScheduler.Default);
                }
            }

            Complete(ref pending[0]);
            Complete(ref pending[1]);
            succeeded = true;
            return planes;
        }
        finally
        {
            if (!succeeded)
            {
                // A transform still running reads these buffers and writes these planes: let it finish before they go back.
                foreach (var task in pending)
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

                foreach (var plane in planes)
                {
                    if (plane is not null)
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
