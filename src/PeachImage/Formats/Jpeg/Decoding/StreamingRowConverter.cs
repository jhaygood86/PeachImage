using System.Buffers;
using PeachImage.Formats.Jpeg.ColorConversion;
using PeachImage.Formats.Jpeg.Decoding.Upsampling;
using PeachImage.Formats.Shared.Parallelism;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Turns component planes into the final image rows while the scan is still being decoded: as inverse-transformed rows
/// become available it upsamples the subsampled chroma rows it needs on the fly and color-converts, straight into the output
/// buffer. That removes the full-resolution chroma planes (and the full passes that made them) from the decode.
/// </summary>
/// <remarks>
/// Produces exactly the bytes the whole-plane path does: the same per-row upsampling and the same row converter, applied
/// to the same inputs. It only handles the shapes where that is a plain per-row operation: a grayscale frame, or a
/// three-component YCbCr frame whose components are not subsampled or are subsampled 2:1 horizontally (with vertical 1 or
/// 2), which covers 4:4:4, 4:2:2 and 4:2:0.
/// </remarks>
internal sealed class StreamingRowConverter
{
    private readonly ComponentDecodeState[] _components;
    private readonly byte[][] _planes;
    private readonly int[] _horizontalRatio;
    private readonly int[] _verticalRatio;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[]? _output;
    private readonly CallerOutput? _target;
    private readonly bool _gray;
    private readonly IChromaUpsampler _upsampler;
    private readonly IColorConverter _converter;
    private readonly int _widestSource;
    private Scratch? _sequentialScratch;
    private int _nextRow;

    private StreamingRowConverter(
        ComponentDecodeState[] components, byte[][] planes, int[] horizontalRatio, int[] verticalRatio,
        int width, int height, byte[]? output, CallerOutput? target, bool gray)
    {
        _target = target;
        _components = components;
        _planes = planes;
        _horizontalRatio = horizontalRatio;
        _verticalRatio = verticalRatio;
        _width = width;
        _height = height;
        _output = output;
        _gray = gray;
        _upsampler = ChromaUpsamplerSelector.Instance;
        _converter = ColorConverterSelector.Instance;
        foreach (var component in components)
        {
            _widestSource = Math.Max(_widestSource, component.ActualWidthInSamples);
        }
    }

    /// <summary>
    /// Creates a converter writing into <paramref name="output"/> (<c>width * height</c> bytes for a grayscale frame,
    /// <c>* 3</c> for YCbCr), or <paramref name="target"/> instead when the caller owns the memory (exactly one of the two is given),
    /// or returns <see langword="null"/> when the frame's shape needs the whole-plane path.
    /// </summary>
    public static StreamingRowConverter? TryCreate(ComponentDecodeState[] components, byte[][] planes, JpegColorSpace colorSpace, int width, int height, byte[]? output, CallerOutput? target = null)
    {
        bool gray = colorSpace == JpegColorSpace.Grayscale && components.Length == 1;
        if (!gray && !(colorSpace == JpegColorSpace.YCbCr && components.Length == 3))
        {
            return null;
        }

        int hMax = 1;
        int vMax = 1;
        foreach (var component in components)
        {
            hMax = Math.Max(hMax, component.Frame.HorizontalSamplingFactor);
            vMax = Math.Max(vMax, component.Frame.VerticalSamplingFactor);
        }

        var horizontalRatio = new int[components.Length];
        var verticalRatio = new int[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            var frame = components[i].Frame;
            if (hMax % frame.HorizontalSamplingFactor != 0 || vMax % frame.VerticalSamplingFactor != 0)
            {
                return null;
            }

            horizontalRatio[i] = hMax / frame.HorizontalSamplingFactor;
            verticalRatio[i] = vMax / frame.VerticalSamplingFactor;

            bool supported = (horizontalRatio[i] == 1 && verticalRatio[i] == 1)
                || (horizontalRatio[i] == 2 && verticalRatio[i] is 1 or 2);
            if (!supported)
            {
                return null;
            }

            // The upsampled plane must cover the image, and an unsubsampled one must match it exactly (it is read in place).
            long upsampledWidth = (long)components[i].ActualWidthInSamples * horizontalRatio[i];
            long upsampledHeight = (long)components[i].ActualHeightInSamples * verticalRatio[i];
            if (upsampledWidth < width || upsampledHeight < height
                || (horizontalRatio[i] == 1 && components[i].ActualWidthInSamples != width))
            {
                return null;
            }
        }

        // Both triangle-filter upsamplers have a row form for the ratios accepted above; the nearest-neighbour one does not.
        if (ChromaUpsamplerSelector.Instance is NearestNeighborUpsampler)
        {
            return null;
        }

        return new StreamingRowConverter(components, planes, horizontalRatio, verticalRatio, width, height, output, target, gray);
    }

    /// <summary>
    /// Converts every output row whose source rows are available, given that the first <paramref name="mcuRowsDone"/> MCU
    /// rows of every component have been inverse-transformed. Calls must come in increasing order of
    /// <paramref name="mcuRowsDone"/> and not overlap.
    /// </summary>
    public void ConvertReady(int mcuRowsDone, ParallelSettings parallel)
    {
        int end = _nextRow;
        while (end < _height && IsReady(end, mcuRowsDone))
        {
            end++;
        }

        int start = _nextRow;
        if (end == start)
        {
            return;
        }

        _nextRow = end;

        // Calls come one at a time, so a sequential call can reuse one scratch for the whole decode and needs no delegates at all.
        if (end - start < RowParallel.MinRowsForParallel || parallel.IsSequential)
        {
            var scratch = _sequentialScratch ??= new Scratch(_widestSource);
            for (int y = start; y < end; y++)
            {
                ConvertRow(y, scratch);
            }

            return;
        }

        ConvertRowsParallel(start, end, parallel);
    }

    private void ConvertRowsParallel(int start, int end, ParallelSettings parallel) =>
        RowParallel.For<Scratch>(
            end - start,
            () => new Scratch(_widestSource),
            (offset, scratch) => ConvertRow(start + offset, scratch),
            scratch => scratch.Return(),
            parallel);

    /// <summary>Returns the scratch rows a sequential conversion kept. The converter converts nothing afterward.</summary>
    public void Release()
    {
        _sequentialScratch?.Return();
        _sequentialScratch = null;
    }

    private bool IsReady(int y, int mcuRowsDone)
    {
        for (int i = 0; i < _components.Length; i++)
        {
            var component = _components[i];
            int actualHeight = component.ActualHeightInSamples;
            int rowsDone = Math.Min(actualHeight, mcuRowsDone * component.BlocksPerMcuVertical * 8);
            if (rowsDone >= actualHeight)
            {
                continue;
            }

            // The last source row this output row reads: its own, plus the neighbour a 2:1 vertical filter blends with.
            int needed = _verticalRatio[i] == 2 ? Math.Min(actualHeight - 1, (y >> 1) + (y & 1)) : y;
            if (needed >= rowsDone)
            {
                return false;
            }
        }

        return true;
    }

    private void ConvertRow(int y, Scratch scratch)
    {
        if (_gray)
        {
            _planes[0].AsSpan(y * _components[0].ActualWidthInSamples, _width).CopyTo(OutputRow(y, _width));
            return;
        }

        var yRow = RowOf(0, y, scratch.Row0, scratch);
        var cbRow = RowOf(1, y, scratch.Row1, scratch);
        var crRow = RowOf(2, y, scratch.Row2, scratch);
        _converter.YCbCrToRgb(yRow, cbRow, crRow, OutputRow(y, _width * 3), _width);
    }

    /// <summary>Row <paramref name="y"/> of the output: the rented buffer's packed row, or the caller's memory at its stride.</summary>
    private unsafe Span<byte> OutputRow(int y, int rowBytes) =>
        _target is { } target
            ? new Span<byte>((byte*)target.Pointer + ((long)y * target.Stride), rowBytes)
            : _output.AsSpan(y * rowBytes, rowBytes);

    /// <summary>Row <paramref name="y"/> of component <paramref name="index"/> at image resolution: a view of the plane when it is not subsampled, an upsampled row in <paramref name="buffer"/> otherwise.</summary>
    private ReadOnlySpan<byte> RowOf(int index, int y, byte[] buffer, Scratch scratch)
    {
        var component = _components[index];
        if (_horizontalRatio[index] == 1 && _verticalRatio[index] == 1)
        {
            return _planes[index].AsSpan(y * component.ActualWidthInSamples, _width);
        }

        _upsampler.TryUpsampleRow(
            _planes[index], component.ActualWidthInSamples, component.ActualHeightInSamples,
            _horizontalRatio[index], _verticalRatio[index], y,
            buffer, scratch.Blend);
        return buffer.AsSpan(0, _width);
    }

    private sealed class Scratch
    {
        public readonly byte[] Row0;
        public readonly byte[] Row1;
        public readonly byte[] Row2;
        public readonly ushort[] Blend;

        public Scratch(int widestSource)
        {
            int rowLength = (widestSource * 2) + 32;
            Row0 = ArrayPool<byte>.Shared.Rent(rowLength);
            Row1 = ArrayPool<byte>.Shared.Rent(rowLength);
            Row2 = ArrayPool<byte>.Shared.Rent(rowLength);
            Blend = ArrayPool<ushort>.Shared.Rent(widestSource + 2);
        }

        public void Return()
        {
            ArrayPool<byte>.Shared.Return(Row0);
            ArrayPool<byte>.Shared.Return(Row1);
            ArrayPool<byte>.Shared.Return(Row2);
            ArrayPool<ushort>.Shared.Return(Blend);
        }
    }
}
