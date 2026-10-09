using System.Buffers;

namespace PeachImage.Formats.Jxl.Modular;

/// <summary>
/// One plane of 32-bit integer samples in a Modular image. Rows are tightly packed (stride equals width). The
/// backing array is rented from the shared pool and returned on <see cref="Dispose"/>; its contents are not
/// cleared, so a new channel must be fully written (or <see cref="Clear"/>ed) before use.
/// </summary>
internal sealed class ModularChannel : IDisposable
{
    private int[]? _data;

    public ModularChannel(int width, int height, int hShift = 0, int vShift = 0)
    {
        if (width < 0 || height < 0 || (long)width * height > int.MaxValue / 2)
        {
            throw new JxlDecodingException("A modular channel has invalid dimensions.");
        }

        Width = width;
        Height = height;
        HShift = hShift;
        VShift = vShift;
        _data = (long)width * height == 0 ? [] : ArrayPool<int>.Shared.Rent(width * height);
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The channel is <c>image_width &gt;&gt; HShift</c> wide; -1 marks a palette (meta) channel.</summary>
    public int HShift { get; set; }

    public int VShift { get; set; }

    /// <summary>The whole plane, <c>Width * Height</c> samples long (the backing array may be longer).</summary>
    public Span<int> Samples => _data.AsSpan(0, Width * Height);

    public Span<int> Row(int y) => _data.AsSpan(y * Width, Width);

    public ReadOnlySpan<int> ReadOnlyRow(int y) => _data.AsSpan(y * Width, Width);

    public int[] Array => _data ?? throw new ObjectDisposedException(nameof(ModularChannel));

    public void Clear() => Samples.Clear();

    /// <summary>Whether this channel has the same size and subsampling as <paramref name="other"/>.</summary>
    public bool SameGeometry(ModularChannel other) =>
        Width == other.Width && Height == other.Height && HShift == other.HShift && VShift == other.VShift;

    /// <summary>Reallocates to <paramref name="width"/> x <paramref name="height"/> (contents are lost).</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height)
        {
            return;
        }

        if (_data is { Length: > 0 })
        {
            ArrayPool<int>.Shared.Return(_data);
        }

        Width = width;
        Height = height;
        _data = (long)width * height == 0 ? [] : ArrayPool<int>.Shared.Rent(width * height);
    }

    public void Dispose()
    {
        if (_data is { Length: > 0 })
        {
            ArrayPool<int>.Shared.Return(_data);
        }

        _data = null;
    }
}

/// <summary>A Modular image: a list of channels plus the transforms that have been applied and must be undone.</summary>
internal sealed class ModularImage : IDisposable
{
    public ModularImage(int width, int height, int bitDepth)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
    }

    public int Width { get; }

    public int Height { get; }

    public int BitDepth { get; }

    /// <summary>The first <see cref="MetaChannelCount"/> channels hold palettes and other side data.</summary>
    public int MetaChannelCount { get; set; }

    public List<ModularChannel> Channels { get; } = [];

    public List<ModularTransform> Transforms { get; set; } = [];

    /// <summary>Adds <paramref name="count"/> full-size channels.</summary>
    public void AddChannels(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Channels.Add(new ModularChannel(Width, Height));
        }
    }

    /// <summary>Undoes all transforms, last applied first.</summary>
    public void UndoTransforms(WeightedPredictorHeader wpHeader)
    {
        while (Transforms.Count > 0)
        {
            var transform = Transforms[^1];
            transform.Inverse(this, wpHeader);
            Transforms.RemoveAt(Transforms.Count - 1);
        }
    }

    public void Dispose()
    {
        foreach (var channel in Channels)
        {
            channel.Dispose();
        }

        Channels.Clear();
    }
}
