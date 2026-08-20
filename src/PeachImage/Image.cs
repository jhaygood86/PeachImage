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
public sealed class Image
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

    private readonly byte[] _pixels;
    private bool _invalidated;

    private Image(int width, int height, PixelFormat pixelFormat, byte[] pixels)
    {
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        _pixels = pixels;
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
    /// <exception cref="InvalidOperationException">
    /// This image was produced by <see cref="AnimatedImage.Frames"/> and a later frame has since been
    /// pulled from the same enumeration — see the <c>Frames</c> remarks for the frame-validity contract.
    /// </exception>
    public Span<byte> GetPixelSpan()
    {
        ThrowIfInvalidated();
        return _pixels;
    }

    /// <summary>
    /// Gets a zero-copy view of a single scanline.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This image was produced by <see cref="AnimatedImage.Frames"/> and a later frame has since been
    /// pulled from the same enumeration — see the <c>Frames</c> remarks for the frame-validity contract.
    /// </exception>
    public Span<byte> GetRowSpan(int y)
    {
        ThrowIfInvalidated();
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);

        int rowBytes = Width * PixelFormat.GetBytesPerPixel();
        return _pixels.AsSpan(y * rowBytes, rowBytes);
    }

    /// <summary>
    /// Gets the entire tightly-packed pixel buffer as <see cref="Memory{T}"/>, for async/non-span consumers.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// This image was produced by <see cref="AnimatedImage.Frames"/> and a later frame has since been
    /// pulled from the same enumeration — see the <c>Frames</c> remarks for the frame-validity contract.
    /// </exception>
    public Memory<byte> PixelMemory
    {
        get
        {
            ThrowIfInvalidated();
            return _pixels;
        }
    }

    /// <summary>Allocates a new, uninitialized image of the given dimensions and pixel format.</summary>
    public static Image Create(int width, int height, PixelFormat pixelFormat)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        int byteCount = checked(width * height * pixelFormat.GetBytesPerPixel());
        return new Image(width, height, pixelFormat, new byte[byteCount]);
    }

    /// <summary>Wraps an already-allocated, tightly-packed pixel buffer without copying it. For use by codec implementations.</summary>
    internal static Image FromBuffer(int width, int height, PixelFormat pixelFormat, byte[] buffer) =>
        new(width, height, pixelFormat, buffer);

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

    /// <summary>
    /// Creates a resized copy of this image using the given target dimensions and resampling filter.
    /// Does not modify this instance (same non-mutating contract as <see cref="Clone"/>).
    /// </summary>
    public Image Resize(int width, int height, ResizeOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        options ??= new ResizeOptions();
        return ImageResizer.Resize(this, width, height, options.Filter);
    }

    /// <summary>
    /// Creates a copy of this image scaled down to fit within a <paramref name="maxWidth"/> x
    /// <paramref name="maxHeight"/> box, preserving aspect ratio. Shrink-only: if this image already fits,
    /// returns this same instance unchanged (unlike <see cref="Resize"/>, which always produces a new
    /// <see cref="Image"/> even for a same-size resize) rather than allocating a needless copy.
    /// </summary>
    public Image ResizeToFit(int maxWidth, int maxHeight, ResizeOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxHeight, 0);

        var (width, height) = ResizeToFitCalculator.ComputeFitDimensions(Width, Height, maxWidth, maxHeight);
        return width == Width && height == Height ? this : Resize(width, height, options);
    }

    /// <summary>Shorthand for <see cref="ResizeToFit(int, int, ResizeOptions?)"/> with a single bounding dimension applied to both axes.</summary>
    public Image ResizeToFit(int maxDimension, ResizeOptions? options = null) =>
        ResizeToFit(maxDimension, maxDimension, options);

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
    /// Marks this image's pixel data as no longer valid. Used internally by animated-decode frame
    /// compositors once a later frame has overwritten the shared canvas this image aliases — see
    /// <see cref="AnimatedImage.Frames"/> for the frame-validity contract this enforces.
    /// </summary>
    internal void Invalidate() => _invalidated = true;

    private void ThrowIfInvalidated()
    {
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
