namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>
/// Owns every <see cref="Jbig2Bitmap"/> created on this thread while it is open and returns their pooled pixel storage when
/// disposed. A decode wraps itself in one scope, so symbols, intermediate regions and the page all go back to the pool together.
/// </summary>
internal sealed class Jbig2BitmapScope : IDisposable
{
    [ThreadStatic]
    private static Jbig2BitmapScope? current;

    private readonly Jbig2BitmapScope? _previous;
    private readonly List<Jbig2Bitmap> _bitmaps = [];

    public Jbig2BitmapScope()
    {
        _previous = current;
        current = this;
    }

    internal static void Register(Jbig2Bitmap bitmap) => current?._bitmaps.Add(bitmap);

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps)
        {
            bitmap.Dispose();
        }

        _bitmaps.Clear();
        current = _previous;
    }
}
