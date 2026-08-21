namespace PeachImage.Formats.Webp.Decoding;

/// <summary>
/// Composites successive decoded ANMF frames onto a persistent RGBA canvas, honoring each frame's blend and
/// dispose-to-background flags. The canvas starts fully transparent — the ANIM chunk's background color field
/// is purely advisory and intentionally unused here, matching every mainstream WebP decoder (and this
/// codebase's own <c>GifFrameCompositor</c>, which follows the identical convention for GIF).
/// </summary>
internal sealed class WebpFrameCompositor
{
    private readonly byte[] _canvas;
    private readonly int _width;
    private readonly int _height;

    private bool _previousDisposeToBackground;
    private (int X, int Y, int Width, int Height) _previousRect;
    private Image? _lastOutput;

    public WebpFrameCompositor(int width, int height)
    {
        _width = width;
        _height = height;
        _canvas = new byte[width * height * 4];
    }

    /// <summary>
    /// Applies the previous frame's disposal, draws <paramref name="frameRgba"/> (already RGBA32, tightly
    /// packed at <paramref name="rect"/>'s own dimensions) at <paramref name="rect"/>'s position — alpha
    /// source-over blended if <paramref name="blend"/>, or an outright overwrite otherwise — and returns an
    /// <see cref="Image"/> that aliases this compositor's persistent canvas — not a copy. That image is
    /// invalidated (its pixel accessors throw) the moment the next <see cref="DrawFrame"/> call runs;
    /// callers must call <see cref="Image.Clone"/> before then if they need to retain the frame. Records
    /// <paramref name="disposeToBackground"/> so the *next* call applies it first, matching WebP's dispose
    /// timing (applied before the next frame is drawn, not immediately after this frame's own duration
    /// elapses) — the same lazy timing as <c>GifFrameCompositor</c>'s disposal handling.
    /// </summary>
    public Image DrawFrame((int X, int Y, int Width, int Height) rect, ReadOnlySpan<byte> frameRgba, bool blend, bool disposeToBackground)
    {
        _lastOutput?.Invalidate();

        if (_previousDisposeToBackground)
        {
            ClearRegion(_previousRect);
        }

        if (blend)
        {
            BlendInto(rect, frameRgba);
        }
        else
        {
            OverwriteInto(rect, frameRgba);
        }

        var output = Image.FromBuffer(_width, _height, PixelFormat.Rgba32, _canvas, owned: false);

        _previousRect = rect;
        _previousDisposeToBackground = disposeToBackground;
        _lastOutput = output;

        return output;
    }

    private void OverwriteInto((int X, int Y, int Width, int Height) rect, ReadOnlySpan<byte> frameRgba)
    {
        for (int y = 0; y < rect.Height; y++)
        {
            int canvasY = rect.Y + y;
            if ((uint)canvasY >= (uint)_height)
            {
                continue;
            }

            for (int x = 0; x < rect.Width; x++)
            {
                int canvasX = rect.X + x;
                if ((uint)canvasX >= (uint)_width)
                {
                    continue;
                }

                int canvasOffset = ((canvasY * _width) + canvasX) * 4;
                int sourceOffset = ((y * rect.Width) + x) * 4;
                frameRgba.Slice(sourceOffset, 4).CopyTo(_canvas.AsSpan(canvasOffset, 4));
            }
        }
    }

    private void BlendInto((int X, int Y, int Width, int Height) rect, ReadOnlySpan<byte> frameRgba)
    {
        for (int y = 0; y < rect.Height; y++)
        {
            int canvasY = rect.Y + y;
            if ((uint)canvasY >= (uint)_height)
            {
                continue;
            }

            for (int x = 0; x < rect.Width; x++)
            {
                int canvasX = rect.X + x;
                if ((uint)canvasX >= (uint)_width)
                {
                    continue;
                }

                int canvasOffset = ((canvasY * _width) + canvasX) * 4;
                int sourceOffset = ((y * rect.Width) + x) * 4;
                BlendPixel(_canvas.AsSpan(canvasOffset, 4), frameRgba.Slice(sourceOffset, 4));
            }
        }
    }

    /// <summary>
    /// Standard 8-bit straight-alpha source-over compositing (integer math). PeachImage has no premultiplied-alpha
    /// buffers anywhere in this codebase, so this is the correct blend for every RGBA32 image it produces.
    /// </summary>
    private static void BlendPixel(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        int srcA = src[3];
        if (srcA == 255)
        {
            src.CopyTo(dst);
            return;
        }

        if (srcA == 0)
        {
            return;
        }

        int dstA = dst[3];
        int outA = srcA + ((dstA * (255 - srcA)) / 255);
        if (outA == 0)
        {
            dst.Clear();
            return;
        }

        for (int c = 0; c < 3; c++)
        {
            int blended = ((src[c] * srcA) + ((dst[c] * dstA * (255 - srcA)) / 255)) / outA;
            dst[c] = (byte)Math.Clamp(blended, 0, 255);
        }

        dst[3] = (byte)outA;
    }

    private void ClearRegion((int X, int Y, int Width, int Height) rect)
    {
        int left = Math.Clamp(rect.X, 0, _width);
        int right = Math.Clamp(rect.X + rect.Width, 0, _width);
        if (right <= left)
        {
            return;
        }

        int rowByteCount = (right - left) * 4;

        for (int y = 0; y < rect.Height; y++)
        {
            int canvasY = rect.Y + y;
            if ((uint)canvasY >= (uint)_height)
            {
                continue;
            }

            int canvasRowOffset = ((canvasY * _width) + left) * 4;
            _canvas.AsSpan(canvasRowOffset, rowByteCount).Clear();
        }
    }
}
