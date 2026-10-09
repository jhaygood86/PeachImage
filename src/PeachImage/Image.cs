using PeachImage.Formats.Avif;
using PeachImage.Formats.Bmp;
using PeachImage.Formats.Gif;
using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jxl;
using PeachImage.Formats.Png;
using PeachImage.Formats.Shared.Compositing;
using PeachImage.Formats.Shared.Metadata;
using PeachImage.Formats.Shared.Orientation;
using PeachImage.Formats.Shared.Resampling;
using PeachImage.Formats.Tiff;
using PeachImage.Formats.Webp;
using PeachImage.Internal;
using PeachImage.Internal.PixelFormatConversion;

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
        TiffCodec.Instance,
        JxlCodec.Instance,
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
    /// Whether this image's source actually carries an alpha channel — not whether its format could ever
    /// have one (see <see cref="ImageFormatInfo.CanDecodeTransparency"/> for that), and computed from the
    /// source's own header/chunk metadata rather than by scanning pixel data. Reflects the source, not the
    /// final <see cref="PixelFormat"/>: converting an opaque source to a target format with an alpha
    /// channel (or an alpha-bearing source down to one without) via <see cref="DecoderOptions.TargetPixelFormat"/>
    /// doesn't change this value. Always <see langword="false"/> for images not produced by a codec's
    /// <c>Decode</c> path (e.g. <see cref="Create"/>).
    /// </summary>
    public bool HasAlpha { get; internal set; }

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

    /// <summary>
    /// Returns this image with <paramref name="orientation"/> applied to its pixels, so that an image whose metadata asks to be
    /// displayed rotated or mirrored (see <see cref="ImageInfo.Orientation"/>) comes out upright. As a new <see cref="Image"/>
    /// (or this same instance, unchanged, for <see cref="ImageOrientation.Normal"/> -- the "may return <c>this</c>" contract
    /// <see cref="Resize"/> documents, including its disposal implications). Does not modify this instance otherwise.
    /// </summary>
    /// <remarks>
    /// Each <see cref="ImageOrientation"/> names the transform applied to the stored pixels to display them upright, so
    /// <see cref="ImageOrientation.Rotate90"/> rotates clockwise. The four orientations that swap width and height produce an image of
    /// <see cref="Height"/> x <see cref="Width"/>, with the horizontal and vertical resolution swapped to match. Resolution and
    /// embedded profiles carry over to the result; an EXIF profile is copied with its orientation tag reset to 1, so a viewer that
    /// honours the tag doesn't transform the already-upright pixels again (this image's own profile is left as it is). Orientation
    /// recorded elsewhere, such as in XMP, is not rewritten. Every <see cref="PixelFormat"/> is supported.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is not a defined orientation.</exception>
    public Image ApplyOrientation(ImageOrientation orientation)
    {
        ThrowIfUndefined(orientation);
        ThrowIfUnusable();
        if (orientation == ImageOrientation.Normal)
        {
            return this;
        }

        bool swaps = ImageOrientationApplier.SwapsDimensions(orientation);
        var result = Create(swaps ? Height : Width, swaps ? Width : Height, PixelFormat);
        ImageOrientationApplier.Apply(orientation, GetPixelSpan(), result.GetPixelSpan(), Width, Height, PixelFormat.GetBytesPerPixel());

        result.Metadata.HorizontalResolution = swaps ? Metadata.VerticalResolution : Metadata.HorizontalResolution;
        result.Metadata.VerticalResolution = swaps ? Metadata.HorizontalResolution : Metadata.VerticalResolution;
        foreach (var profile in Metadata.Profiles)
        {
            result.Metadata.Profiles.Add(WithUprightOrientation(profile));
        }

        result.HasAlpha = HasAlpha;
        result.IsAnimated = IsAnimated;
        return result;
    }

    /// <summary>
    /// Writes this image with <paramref name="orientation"/> applied to <paramref name="destination"/>, without allocating: the
    /// pixel work of <see cref="ApplyOrientation(ImageOrientation)"/> into an image the caller already owns (for example one rented
    /// once and reused across a batch). This image is not modified.
    /// </summary>
    /// <remarks>
    /// Only pixels are written; <paramref name="destination"/>'s <see cref="Metadata"/>, <see cref="HasAlpha"/> and
    /// <see cref="IsAnimated"/> are left as they are, because carrying this image's across would allocate. A caller that copies
    /// an EXIF profile over should reset its orientation tag itself.
    /// </remarks>
    /// <param name="orientation">The orientation to apply.</param>
    /// <param name="destination">
    /// A different image with the same <see cref="PixelFormat"/> and dimensions of <see cref="Width"/> x <see cref="Height"/>, or
    /// <see cref="Height"/> x <see cref="Width"/> for the orientations that swap them.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is not a defined orientation.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is this image or shares its pixel buffer (the transform cannot be done while reading from the
    /// pixels it overwrites -- use <see cref="ApplyOrientationInPlace"/>), or its pixel format or dimensions don't match.
    /// </exception>
    public void ApplyOrientation(ImageOrientation orientation, Image destination)
    {
        ThrowIfUndefined(orientation);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfUnusable();
        destination.ThrowIfUnusable();
        if (ReferenceEquals(this, destination) || ReferenceEquals(_pixels, destination._pixels))
        {
            throw new ArgumentException("The destination must be a different image than the source. Use ApplyOrientationInPlace to transform an image in place.", nameof(destination));
        }

        if (destination.PixelFormat != PixelFormat)
        {
            throw new ArgumentException($"The destination's pixel format is {destination.PixelFormat} but the source's is {PixelFormat}.", nameof(destination));
        }

        bool swaps = ImageOrientationApplier.SwapsDimensions(orientation);
        int expectedWidth = swaps ? Height : Width;
        int expectedHeight = swaps ? Width : Height;
        if (destination.Width != expectedWidth || destination.Height != expectedHeight)
        {
            throw new ArgumentException(
                $"Applying {orientation} to a {Width}x{Height} image produces {expectedWidth}x{expectedHeight}, but the destination is {destination.Width}x{destination.Height}.",
                nameof(destination));
        }

        ImageOrientationApplier.Apply(orientation, GetPixelSpan(), destination.GetPixelSpan(), Width, Height, PixelFormat.GetBytesPerPixel());
    }

    /// <summary>
    /// Applies <paramref name="orientation"/> to this image's own pixels without allocating. Only possible when the result has the
    /// same dimensions: always for <see cref="ImageOrientation.Normal"/>, <see cref="ImageOrientation.MirrorHorizontal"/>,
    /// <see cref="ImageOrientation.Rotate180"/> and <see cref="ImageOrientation.MirrorVertical"/>, and for the other four only on a
    /// square image.
    /// </summary>
    /// <remarks>Metadata is not changed (neither the resolution of a square image nor an EXIF orientation tag), so a caller that keeps the image's EXIF profile should reset its orientation tag itself; <see cref="ApplyOrientation(ImageOrientation)"/> does that for you.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is not a defined orientation.</exception>
    /// <exception cref="InvalidOperationException">The orientation swaps width and height and this image is not square: use <see cref="ApplyOrientation(ImageOrientation)"/> or <see cref="ApplyOrientation(ImageOrientation, Image)"/>.</exception>
    public void ApplyOrientationInPlace(ImageOrientation orientation)
    {
        ThrowIfUndefined(orientation);
        ThrowIfUnusable();
        ImageOrientationApplier.ApplyInPlace(orientation, GetPixelSpan(), Width, Height, PixelFormat.GetBytesPerPixel());
    }

    private static void ThrowIfUndefined(ImageOrientation orientation)
    {
        if (!Enum.IsDefined(orientation))
        {
            throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Not a defined orientation.");
        }
    }

    // The profile as it should accompany pixels that have been made upright: an EXIF blob is copied with its orientation tag reset to 1
    // (the shared RawMetadataProfile and its bytes are never mutated); every other profile, and EXIF with nothing to reset, is shared as-is.
    private static RawMetadataProfile WithUprightOrientation(RawMetadataProfile profile)
    {
        if (profile.Kind != MetadataProfileKind.Exif || ExifOrientationReader.Read(profile.Data) == ImageOrientation.Normal)
        {
            return profile;
        }

        var data = (byte[])profile.Data.Clone();
        return ExifOrientationResetter.TryResetOrientation(data)
            ? new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = data }
            : profile;
    }

    /// <summary>
    /// Converts this image to <paramref name="targetFormat"/>, returning a new <see cref="Image"/> (or this same instance, unchanged,
    /// when it already has that format -- the "may return <c>this</c>" contract <see cref="Resize"/> documents). Supports
    /// every combination of the gray, RGB and RGBA formats at 8-bit, 16-bit and 32-bit float depth, so that, for example, an
    /// HDR <see cref="PixelFormat.RgbaF32"/> JPEG XL decode can be brought to an encodable <see cref="PixelFormat.Rgb24"/>.
    /// </summary>
    /// <remarks>
    /// Values are converted as stored: no transfer function, gamut or tone mapping is applied. Integer targets round to the
    /// nearest value and clamp to the format range (float values below 0 or above 1 saturate). Colour to gray uses the
    /// BT.601 luma weights; a target without alpha discards the alpha channel (colour is not composited onto a background);
    /// a source without alpha gets opaque alpha. Resolution and embedded profiles carry over to the result.
    /// <see cref="PixelFormat.Cmyk32"/> and <see cref="PixelFormat.Ycck32"/> are not supported in either direction --
    /// use <see cref="ConvertToSrgb"/> for CMYK.
    /// </remarks>
    /// <exception cref="NotSupportedException">This image format or <paramref name="targetFormat"/> is CMYK or YCCK.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetFormat"/> is not a defined pixel format.</exception>
    public Image ConvertTo(PixelFormat targetFormat)
    {
        if (!Enum.IsDefined(targetFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(targetFormat));
        }

        ThrowIfUnusable();
        if (targetFormat == PixelFormat)
        {
            return this;
        }

        if (!ImagePixelConverter.IsConvertible(PixelFormat) || !ImagePixelConverter.IsConvertible(targetFormat))
        {
            throw new NotSupportedException($"Converting {PixelFormat} to {targetFormat} is not supported. Use ConvertToSrgb for CMYK images.");
        }

        var result = ImagePixelConverter.Convert(this, targetFormat);
        result.Metadata.HorizontalResolution = Metadata.HorizontalResolution;
        result.Metadata.VerticalResolution = Metadata.VerticalResolution;
        foreach (var profile in Metadata.Profiles)
        {
            result.Metadata.Profiles.Add(profile);
        }

        result.HasAlpha = HasAlpha;
        result.IsAnimated = IsAnimated;
        return result;
    }

    /// <summary>
    /// Color-manages this image using its own embedded ICC profile (<see cref="ImageMetadata.GetIccColorProfile"/>),
    /// producing a new <see cref="PixelFormat.Rgba32"/> <see cref="Image"/> — the high-level convenience over
    /// <see cref="IccColorProfile.ConvertToSrgb"/> for the common case of "just color-manage this image for me."
    /// A missing or unusable embedded profile is a normal, expected outcome (most images don't carry one) rather
    /// than an error: when there's nothing to convert — no usable profile, its channel count doesn't match this
    /// image's own pixel format, or <see cref="PixelFormat"/> isn't one ICC conversion applies to (it's already
    /// <see cref="PixelFormat.Rgba32"/>, for instance) — this same instance is returned unchanged rather than
    /// throwing or allocating a needless copy, the same "may return <c>this</c>" contract <see cref="Resize"/>
    /// documents (see its own remarks for the disposal implications of that).
    /// </summary>
    public Image ConvertToSrgb()
    {
        if (PixelFormat is not (PixelFormat.Gray8 or PixelFormat.Rgb24 or PixelFormat.Cmyk32))
        {
            return this;
        }

        var iccProfile = Metadata.GetIccColorProfile();
        if (iccProfile is null || iccProfile.ChannelCount != PixelFormat.GetChannelCount())
        {
            return this;
        }

        var result = Create(Width, Height, PixelFormat.Rgba32);
        iccProfile.ConvertToSrgb(GetPixelSpan(), result.GetPixelSpan(), Width * Height);

        result.Metadata.HorizontalResolution = Metadata.HorizontalResolution;
        result.Metadata.VerticalResolution = Metadata.VerticalResolution;
        foreach (var profile in Metadata.Profiles)
        {
            result.Metadata.Profiles.Add(profile);
        }

        result.HasAlpha = false;
        return result;
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
    /// image already fits within <paramref name="width"/> x <paramref name="height"/>, or when it's
    /// <see cref="ResizeMode.Crop"/>/<see cref="ResizeMode.Pad"/> and this image is already exactly
    /// <paramref name="width"/> x <paramref name="height"/>, in which case this same instance is returned
    /// unchanged rather than allocating a needless copy. Because of that fast path, the returned
    /// <see cref="Image"/> may be <em>this same instance</em> rather than an independent one — disposing one
    /// of the two references then makes the other throw <see cref="ObjectDisposedException"/> on its next
    /// access, same as disposing any other shared reference twice would. If you need the source and the
    /// resized result to have independent lifetimes regardless of which path is taken, dispose only after
    /// you're done with both, or check <see cref="object.ReferenceEquals(object?, object?)"/> first.
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
        else if (options.Mode is ResizeMode.Crop or ResizeMode.Pad)
        {
            if (width == Width && height == Height)
            {
                return this;
            }

            var plan = ResizeFramingPlanner.Plan(Width, Height, width, height, options.Mode, options.Anchor);
            var intermediate = ImageResizer.Resize(this, plan.IntermediateWidth, plan.IntermediateHeight, options.Filter);

            bool needsFraming = plan.OffsetX != 0 || plan.OffsetY != 0
                || plan.IntermediateWidth != width || plan.IntermediateHeight != height;
            if (!needsFraming)
            {
                return intermediate;
            }

            try
            {
                return options.Mode == ResizeMode.Crop
                    ? ImageFramer.Crop(intermediate, width, height, plan.OffsetX, plan.OffsetY)
                    : ImageFramer.Pad(intermediate, width, height, plan.OffsetX, plan.OffsetY, options.BackgroundColor);
            }
            finally
            {
                intermediate.Dispose();
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
        ThrowIfFloatPixels();
        codec.Encode(this, fileStream, options);
    }

    /// <summary>Encodes this image as <paramref name="formatName"/> and writes it to <paramref name="stream"/>.</summary>
    public void Save(Stream stream, string formatName, EncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatName);

        var codec = FindCodecByFormatName(formatName)
            ?? throw new UnknownImageFormatException($"No built-in codec can encode format '{formatName}'.", formatName);

        ThrowIfFloatPixels();
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
        ThrowIfFloatPixels();
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

        ThrowIfFloatPixels();
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

    // No built-in encoder accepts 32-bit float samples; fail loudly rather than let one misread them as bytes.
    private void ThrowIfFloatPixels()
    {
        if (PixelFormat.IsFloat())
        {
            throw new NotSupportedException($"No built-in encoder supports {PixelFormat} pixels. Convert the image to an integer format first (Image.ConvertTo) to encode it.");
        }
    }

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
