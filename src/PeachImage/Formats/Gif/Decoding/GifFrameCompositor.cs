namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Composites successive decoded GIF frames onto a persistent RGBA canvas, honoring each frame's disposal
/// method. The canvas starts fully transparent (not filled with the Global Color Table's background color) —
/// the de facto convention every mainstream decoder (browsers, Skia) follows, avoiding a background-color
/// flash for the common case of a transparent-background animation.
/// </summary>
internal sealed class GifFrameCompositor
{
    private readonly byte[] _canvas;
    private readonly int _width;
    private readonly int _height;

    private GifDisposalMethod _previousDisposal = GifDisposalMethod.None;
    private (int Left, int Top, int Width, int Height) _previousRect;
    private byte[]? _previousSnapshot;
    private Image? _lastOutput;

    public GifFrameCompositor(int width, int height)
    {
        _width = width;
        _height = height;
        _canvas = new byte[width * height * 4];
    }

    /// <summary>
    /// Applies the previous frame's disposal, draws <paramref name="indices"/> (already de-interlaced, in
    /// display row order) at <paramref name="descriptor"/>'s position using <paramref name="palette"/>, and
    /// returns an <see cref="Image"/> that aliases this compositor's persistent canvas — not a copy. That
    /// image is invalidated (its pixel accessors throw) the moment the next <see cref="DrawFrame"/> call
    /// runs; callers must call <see cref="Image.Clone"/> before then if they need to retain the frame.
    /// </summary>
    public Image DrawFrame(GifImageDescriptor descriptor, byte[] indices, byte[] palette, int? transparentIndex, GifDisposalMethod disposal)
    {
        _lastOutput?.Invalidate();

        ApplyPreviousDisposal();

        var rect = (descriptor.Left, descriptor.Top, descriptor.Width, descriptor.Height);
        byte[]? snapshotForThisFrame = disposal == GifDisposalMethod.RestoreToPrevious
            ? CaptureRegion(rect)
            : null;

        DrawIndices(rect, indices, palette, transparentIndex);

        var output = Image.FromBuffer(_width, _height, PixelFormat.Rgba32, _canvas);

        _previousDisposal = disposal;
        _previousRect = rect;
        _previousSnapshot = snapshotForThisFrame;
        _lastOutput = output;

        return output;
    }

    private void ApplyPreviousDisposal()
    {
        switch (_previousDisposal)
        {
            case GifDisposalMethod.RestoreToBackground:
                ClearRegion(_previousRect);
                break;
            case GifDisposalMethod.RestoreToPrevious when _previousSnapshot is not null:
                RestoreRegion(_previousRect, _previousSnapshot);
                break;
        }
    }

    private void DrawIndices((int Left, int Top, int Width, int Height) rect, byte[] indices, byte[] palette, int? transparentIndex)
    {
        int paletteEntryCount = palette.Length / 3;

        for (int y = 0; y < rect.Height; y++)
        {
            int canvasY = rect.Top + y;
            if ((uint)canvasY >= (uint)_height)
            {
                continue;
            }

            for (int x = 0; x < rect.Width; x++)
            {
                int canvasX = rect.Left + x;
                if ((uint)canvasX >= (uint)_width)
                {
                    continue;
                }

                int index = indices[(y * rect.Width) + x];
                if (transparentIndex.HasValue && index == transparentIndex.Value)
                {
                    continue;
                }

                if ((uint)index >= (uint)paletteEntryCount)
                {
                    continue;
                }

                int canvasOffset = ((canvasY * _width) + canvasX) * 4;
                int paletteOffset = index * 3;
                _canvas[canvasOffset] = palette[paletteOffset];
                _canvas[canvasOffset + 1] = palette[paletteOffset + 1];
                _canvas[canvasOffset + 2] = palette[paletteOffset + 2];
                _canvas[canvasOffset + 3] = 255;
            }
        }
    }

    private byte[] CaptureRegion((int Left, int Top, int Width, int Height) rect)
    {
        byte[] snapshot = new byte[rect.Width * rect.Height * 4];
        ForEachClampedRow(rect, (canvasRowOffset, regionRowOffset, byteCount) =>
            _canvas.AsSpan(canvasRowOffset, byteCount).CopyTo(snapshot.AsSpan(regionRowOffset, byteCount)));

        return snapshot;
    }

    private void RestoreRegion((int Left, int Top, int Width, int Height) rect, byte[] snapshot)
    {
        ForEachClampedRow(rect, (canvasRowOffset, regionRowOffset, byteCount) =>
            snapshot.AsSpan(regionRowOffset, byteCount).CopyTo(_canvas.AsSpan(canvasRowOffset, byteCount)));
    }

    private void ClearRegion((int Left, int Top, int Width, int Height) rect)
    {
        ForEachClampedRow(rect, (canvasRowOffset, _, byteCount) =>
            _canvas.AsSpan(canvasRowOffset, byteCount).Clear());
    }

    private void ForEachClampedRow((int Left, int Top, int Width, int Height) rect, Action<int, int, int> action)
    {
        int left = Math.Clamp(rect.Left, 0, _width);
        int right = Math.Clamp(rect.Left + rect.Width, 0, _width);
        if (right <= left)
        {
            return;
        }

        int rowByteCount = (right - left) * 4;

        for (int y = 0; y < rect.Height; y++)
        {
            int canvasY = rect.Top + y;
            if ((uint)canvasY >= (uint)_height)
            {
                continue;
            }

            int canvasRowOffset = ((canvasY * _width) + left) * 4;
            int regionRowOffset = ((y * rect.Width) + (left - rect.Left)) * 4;
            action(canvasRowOffset, regionRowOffset, rowByteCount);
        }
    }
}
