using PeachImage.Formats.Avif;
using PeachImage.Formats.Bmp;
using PeachImage.Formats.Gif;
using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Png;
using PeachImage.Formats.Shared.Resampling;
using PeachImage.Formats.Webp;
using PeachImage.Internal;

namespace PeachImage;

/// <summary>
/// An in-memory, single-frame, tightly-packed pixel buffer decoded from (or destined for) an image file.
/// </summary>
/// <remarks>
/// Implements <see cref="IDisposable"/> because most instances rent their pixel buffer from a shared
/// <see cref="System.Buffers.ArrayPool{T}"/> and return it on <see cref="Dispose"/>. Disposal is an
/// opt-in performance mechanism, not a correctness requirement: an un-disposed <see cref="Image"/>'s
/// buffer is simply garbage-collected like any other array, with no leak or corruption risk — but
/// disposing promptly lets the pool reuse that buffer for the next decode/resize, which matters most
/// under concurrent load (e.g. a service processing many uploads at once). Images produced by
/// <see cref="AnimatedImage.Frames"/> (as opposed to <see cref="Image.Clone"/>) don't own a pooled
/// buffer at all — they alias decoder-internal state — so <see cref="Dispose"/> on those is always a
/// safe no-op.
/// </remarks>
public sealed class Image : IDisposable
{
    /// <summary>
    /// The fixed set of built-in codecs. Internal rather than private so <see cref="AnimatedImage"/> can
    /// filter it down to the subset that also implement <see cref="IAnimatedImageCodec"/>, instead of
    /// maintaining a second, separately-curated codec list.
    /// </summary>
    internal static readonly IImageCodec[] Codecs =
    [
        JpegCodec.Instance,
        BmpCodec.Instance,
        PngCodec.Instance,
        GifCodec.Instance,
        WebpCodec.Instance,
        AvifCodec.Instance,
    ];

    private static readonly int MaxHeaderSize = Codecs.Max(codec => codec.HeaderSize);

    private static readonly ImageFormatInfo[] FormatInfos =
        [.. Codecs.Select(codec => new ImageFormatInfo(
            codec.FormatName, codec.FileExtensions, codec.MimeTypes,
            codec.CanDecode, codec.CanEncode, codec.CanDecodeTransparency, codec.CanEncodeTransparency))];

    private readonly byte[] _pixels;
    private readonly int _byteLength;
    private readonly bool _owned;
    private bool _invalidated;
    private bool _disposed;

    private Image(int width, int height, PixelFormat pixelFormat, byte[] pixels, bool owned)
    {
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        _pixels = pixels;
        _byteLength = checked(width * height * pixelFormat.GetBytesPerPixel());
        _owned = owned;
        Metadata = new ImageMetadata();
    }

    /// <summary>The image width, in pixels.</summary>
    public int Width { get; }

    /// <summary>The image height, in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixel buffer's layout.</summary>
    public PixelFormat PixelFormat { get; }

    /// <summary>Metadata (EXIF/ICC/etc.) captured alongside the pixel data, if any.</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>
    /// Whether this <see cref="Image"/> was decoded from a multi-frame animated source (only its first frame
    /// was decoded — see <see cref="AnimatedImage"/> to decode every frame). Always <see langword="false"/>
    /// for images not produced by a codec's <c>Decode</c> path (e.g. <see cref="Create"/>).
    /// </summary>
    public bool IsAnimated { get; internal set; }

    /// <summary>
    /// Gets a zero-copy view of the entire tightly-packed pixel buffer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This image has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// This image was produced by <see cref="AnimatedImage.Frames"/> and a later frame has since been
    /// pulled from the same enumeration — see the <c>Frames</c> remarks for the frame-validity contract.
    /// </exception>
    public Span<byte> GetPixelSpan()
    {
        ThrowIfUnusable();
        return _pixels.AsSpan(0, _byteLength);
    }

    /// <summary>
    /// Gets a zero-copy view of a single scanline.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This image has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// This image was produced by <see cref="AnimatedImage.Frames"/> and a later frame has since been
    /// pulled from the same enumeration — see the <c>Frames</c> remarks for the frame-validity contract.
    /// </exception>
    public Span<byte> GetRowSpan(int y)
    {
        ThrowIfUnusable();
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);

        int rowBytes = Width * PixelFormat.GetBytesPerPixel();
        return _pixels.AsSpan(y * rowBytes, rowBytes);
    }

    /// <summary>
    /// Gets the entire tightly-packed pixel buffer as <see cref="Memory{T}"/>, for async/non-span consumers.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This image has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// This image was produced by <see cref="AnimatedImage.Frames"/> and a later frame has since been
    /// pulled from the same enumeration — see the <c>Frames</c> remarks for the frame-validity contract.
    /// </exception>
    public Memory<byte> PixelMemory
    {
        get
        {
            ThrowIfUnusable();
            return _pixels.AsMemory(0, _byteLength);
        }
    }

    /// <summary>
    /// Allocates a new, uninitialized image of the given dimensions and pixel format. The backing buffer
    /// is rented from a shared pool — see the type-level remarks on disposal.
    /// </summary>
    public static Image Create(int width, int height, PixelFormat pixelFormat)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        int byteCount = checked(width * height * pixelFormat.GetBytesPerPixel());
        return new Image(width, height, pixelFormat, ImageBufferPool.Shared.Rent(byteCount), owned: true);
    }

    /// <summary>
    /// Wraps an already-allocated, tightly-packed pixel buffer without copying it. For use by codec
    /// implementations. <paramref name="owned"/> must be <see langword="true"/> only when
    /// <paramref name="buffer"/> was itself rented from <see cref="ImageBufferPool.Shared"/> — passing
    /// <see langword="true"/> for a buffer that wasn't would return a foreign array to the pool on
    /// <see cref="Dispose"/>. Pass <see langword="false"/> for buffers with a lifetime the caller manages
    /// itself (e.g. a persistent animation-compositor canvas aliased by multiple <see cref="Image"/>
    /// instances over time), for which <see cref="Dispose"/> is then a safe no-op.
    /// </summary>
    internal static Image FromBuffer(int width, int height, PixelFormat pixelFormat, byte[] buffer, bool owned) =>
        new(width, height, pixelFormat, buffer, owned);

    /// <summary>
    /// Creates an independent copy of this image's pixel data and metadata. Use this to retain a frame
    /// pulled from <see cref="AnimatedImage.Frames"/> beyond the point where it would otherwise be
    /// invalidated by advancing to the next frame.
    /// </summary>
    public Image Clone()
    {
        var copy = Create(Width, Height, PixelFormat);
        GetPixelSpan().CopyTo(copy.GetPixelSpan());
        copy.Metadata.HorizontalResolution = Metadata.HorizontalResolution;
        copy.Metadata.VerticalResolution = Metadata.VerticalResolution;
        foreach (var profile in Metadata.Profiles)
        {
            copy.Metadata.Profiles.Add(profile);
        }

        return copy;
    }

    /// <summary>Loads an image from <paramref name="path"/>, auto-detecting its format.</summary>
    public static Image Load(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fileStream = File.OpenRead(path);
        return Load(fileStream, options);
    }

    /// <summary>Loads an image from <paramref name="stream"/>, auto-detecting its format by sniffing its header bytes.</summary>
    public static Image Load(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var codec = ResolveCodec(stream, out var preparedStream);
        if (codec is null)
        {
            throw new UnknownImageFormatException("The image format could not be determined from the stream contents.");
        }

        return codec.Decode(preparedStream, options);
    }

    /// <summary>Attempts to load an image from <paramref name="stream"/>, returning <see langword="false"/> instead of throwing on failure.</summary>
    public static bool TryLoad(Stream stream, out Image? image, DecoderOptions? options = null)
    {
        try
        {
            image = Load(stream, options);
            return true;
        }
        catch (ImageFormatException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>Asynchronously loads an image by buffering <paramref name="stream"/> and then decoding it synchronously (decoding itself is CPU-bound, not I/O-bound).</summary>
    public static async Task<Image> LoadAsync(Stream stream, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        buffered.Position = 0;
        return Load(buffered, options);
    }

    /// <summary>
    /// Loads an image from an in-memory buffer, auto-detecting its format by sniffing its header bytes.
    /// Decodes directly from <paramref name="data"/>'s pinned memory via <see cref="UnmanagedMemoryStream"/>
    /// — no intermediate copy, unlike wrapping a <see cref="MemoryStream"/> around <c>data.ToArray()</c>.
    /// </summary>
    public static unsafe Image Load(ReadOnlySpan<byte> data, DecoderOptions? options = null)
    {
        fixed (byte* pointer = data)
        {
            using var stream = new UnmanagedMemoryStream(pointer, data.Length);
            return Load(stream, options);
        }
    }

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var codec = ResolveCodec(stream, out var preparedStream);
        if (codec is null)
        {
            throw new UnknownImageFormatException("The image format could not be determined from the stream contents.");
        }

        return codec.Identify(preparedStream);
    }

    /// <summary>Capability and identity metadata for every format PeachImage has a built-in codec for.</summary>
    public static IReadOnlyList<ImageFormatInfo> SupportedFormats => FormatInfos;

    /// <summary>Looks up capability and identity metadata for the format named <paramref name="formatName"/>.</summary>
    /// <exception cref="UnknownImageFormatException">No built-in codec is named <paramref name="formatName"/>.</exception>
    public static ImageFormatInfo GetFormatInfo(string formatName)
    {
        ArgumentNullException.ThrowIfNull(formatName);

        foreach (var info in FormatInfos)
        {
            if (string.Equals(info.FormatName, formatName, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        throw new UnknownImageFormatException($"No built-in codec for format '{formatName}'.", formatName);
    }

    /// <summary>
    /// Creates a resized copy of this image using the given target dimensions and resampling filter. Does
    /// not modify this instance (same non-mutating contract as <see cref="Clone"/>) — except when
    /// <paramref name="options"/>'s <see cref="ResizeOptions.Mode"/> is <see cref="ResizeMode.Max"/> and this
    /// image already fits within <paramref name="width"/> x <paramref name="height"/>, in which case this
    /// same instance is returned unchanged rather than allocating a needless copy. Because of that fast
    /// path, the returned <see cref="Image"/> may be <em>this same instance</em> rather than an independent
    /// one — disposing one of the two references then makes the other throw <see cref="ObjectDisposedException"/>
    /// on its next access, same as disposing any other shared reference twice would. If you need the source
    /// and the resized result to have independent lifetimes regardless of which path is taken, dispose only
    /// after you're done with both, or check <see cref="object.ReferenceEquals(object?, object?)"/> first.
    /// </summary>
    public Image Resize(int width, int height, ResizeOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        options ??= new ResizeOptions();
        if (options.Mode == ResizeMode.Max)
        {
            (width, height) = ResizeToFitCalculator.ComputeFitDimensions(Width, Height, width, height);
            if (width == Width && height == Height)
            {
                return this;
            }
        }

        return ImageResizer.Resize(this, width, height, options.Filter);
    }

    /// <summary>Encodes this image and writes it to <paramref name="path"/>, inferring the format from the file extension.</summary>
    public void Save(string path, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        string extension = Path.GetExtension(path).TrimStart('.');
        var codec = FindCodecByExtension(extension)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode files with extension '.{extension}'.");

        using var fileStream = File.Create(path);
        codec.Encode(this, fileStream, options);
    }

    /// <summary>Encodes this image as <paramref name="formatName"/> and writes it to <paramref name="stream"/>.</summary>
    public void Save(Stream stream, string formatName, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatName);

        var codec = FindCodecByFormatName(formatName)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode format '{formatName}'.", formatName);

        codec.Encode(this, stream, options);
    }

    /// <summary>
    /// Encodes this image and writes it to <paramref name="path"/>, inferring the format from the file
    /// extension. Encoding itself is synchronous and CPU-bound, same as <see cref="LoadAsync(Stream, DecoderOptions?, CancellationToken)"/>'s
    /// decode — only the file write is awaited.
    /// </summary>
    public async Task SaveAsync(string path, EncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        string extension = Path.GetExtension(path).TrimStart('.');
        var codec = FindCodecByExtension(extension)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode files with extension '.{extension}'.");

        using var fileStream = File.Create(path);
        using var buffered = new MemoryStream();
        codec.Encode(this, buffered, options);
        buffered.Position = 0;
        await buffered.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Encodes this image as <paramref name="formatName"/> and writes it to <paramref name="stream"/>.
    /// Encoding itself is synchronous and CPU-bound, same as <see cref="LoadAsync(Stream, DecoderOptions?, CancellationToken)"/>'s
    /// decode — only the write to <paramref name="stream"/> is awaited.
    /// </summary>
    public async Task SaveAsync(Stream stream, string formatName, EncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatName);

        var codec = FindCodecByFormatName(formatName)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode format '{formatName}'.", formatName);

        using var buffered = new MemoryStream();
        codec.Encode(this, buffered, options);
        buffered.Position = 0;
        await buffered.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks this image's pixel data as no longer valid. Used internally by animated-decode frame
    /// compositors once a later frame has overwritten the shared canvas this image aliases — see
    /// <see cref="AnimatedImage.Frames"/> for the frame-validity contract this enforces. Independent of
    /// <see cref="Dispose"/>: invalidation is compositor-driven and applies to non-owned (aliased) images,
    /// while disposal is caller-driven and only ever returns a buffer for images that own one.
    /// </summary>
    internal void Invalidate() => _invalidated = true;

    /// <summary>
    /// Returns this image's pixel buffer to its pool, if it owns one (see the type-level remarks on
    /// disposal). Safe to call more than once. After calling this, <see cref="GetPixelSpan"/>,
    /// <see cref="GetRowSpan"/>, and <see cref="PixelMemory"/> throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_owned)
        {
            ImageBufferPool.Shared.Return(_pixels);
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_invalidated)
        {
            throw new InvalidOperationException(
                "This frame's Image has been invalidated because a later frame was requested from the same " +
                "AnimatedImage.Frames enumeration. Call Clone() (on the Image or the AnimatedImageFrame) " +
                "before advancing if you need to retain this frame's pixel data.");
        }
    }

    private static IImageCodec? ResolveCodec(Stream stream, out Stream preparedStream)
    {
        var header = new byte[MaxHeaderSize];
        int read = ReadFully(stream, header);
        var headerSpan = header.AsSpan(0, read);

        IImageCodec? found = null;
        foreach (var codec in Codecs)
        {
            if (codec.CanDecode && codec.IsSupportedFileFormat(headerSpan))
            {
                found = codec;
                break;
            }
        }

        if (stream.CanSeek)
        {
            stream.Seek(-read, SeekOrigin.Current);
            preparedStream = stream;
        }
        else
        {
            preparedStream = new PrefixedStream(header.AsSpan(0, read).ToArray(), stream);
        }

        return found;
    }

    private static IImageCodec? FindCodecByFormatName(string formatName)
    {
        foreach (var codec in Codecs)
        {
            if (codec.CanEncode && string.Equals(codec.FormatName, formatName, StringComparison.OrdinalIgnoreCase))
            {
                return codec;
            }
        }

        return null;
    }

    private static IImageCodec? FindCodecByExtension(string extension)
    {
        foreach (var codec in Codecs)
        {
            if (!codec.CanEncode)
            {
                continue;
            }

            foreach (var candidate in codec.FileExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return codec;
                }
            }
        }

        return null;
    }

    /// <summary>Shared by <see cref="AnimatedImage"/>'s own header-sniffing so both types read a stream's header identically.</summary>
    internal static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
