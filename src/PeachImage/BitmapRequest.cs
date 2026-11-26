namespace PeachImage;

/// <summary>A rectangle of pixels, in pixels from the top-left corner.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct ImageRegion(int X, int Y, int Width, int Height);

/// <summary>What <see cref="BitmapRequest.Orientation"/> does about the image's EXIF orientation and vertical flip.</summary>
public enum BitmapOrientation
{
    /// <summary>Apply the EXIF orientation so the pixels come out upright (the <c>createImageBitmap</c> value <c>"from-image"</c>).</summary>
    FromImage,

    /// <summary>Apply the EXIF orientation and then flip the result vertically (<c>"flipY"</c>).</summary>
    FlipY,

    /// <summary>Leave the stored pixel order alone and ignore the EXIF orientation.</summary>
    None,
}

/// <summary>Whether <see cref="BitmapRequest"/> premultiplies color by alpha (<c>premultiplyAlpha</c>).</summary>
public enum BitmapPremultiply
{
    /// <summary>The library's choice for a canvas: premultiplied, the layout a canvas stores. Same as <see cref="Premultiply"/>.</summary>
    Default,

    /// <summary>Keep straight (non-premultiplied) alpha.</summary>
    None,

    /// <summary>Multiply color by alpha, giving a <c>Premultiplied*</c> pixel format for images that have alpha.</summary>
    Premultiply,
}

/// <summary>Whether <see cref="BitmapRequest"/> converts into <see cref="BitmapRequest.TargetColorSpace"/> (<c>colorSpaceConversion</c>).</summary>
public enum BitmapColorSpaceConversion
{
    /// <summary>Convert from the color space the image declares (sRGB when it declares none) into <see cref="BitmapRequest.TargetColorSpace"/>.</summary>
    Default,

    /// <summary>Do not convert: the stored values are taken to already be in <see cref="BitmapRequest.TargetColorSpace"/> and the result is labelled as such.</summary>
    None,
}

/// <summary>The resampling quality of a <see cref="BitmapRequest"/> resize (<c>resizeQuality</c>), mapped onto <see cref="ResamplingFilter"/>.</summary>
public enum ResizeQuality
{
    /// <summary>Nearest neighbor (<see cref="ResamplingFilter.NearestNeighbor"/>): blocky, fastest.</summary>
    Pixelated,

    /// <summary>Bilinear (<see cref="ResamplingFilter.Bilinear"/>): the <c>createImageBitmap</c> default.</summary>
    Low,

    /// <summary>Bicubic (<see cref="ResamplingFilter.Bicubic"/>).</summary>
    Medium,

    /// <summary>Lanczos 3 (<see cref="ResamplingFilter.Lanczos3"/>): sharpest, slowest.</summary>
    High,
}

/// <summary>
/// The options of an HTML <c>createImageBitmap</c> call, applied by <see cref="Image.LoadBitmap(Stream, BitmapRequest)"/> in a fixed order:
/// orientation, crop, color space conversion, resize, premultiplication. Color conversion and resizing run on the image's own sample type
/// (16-bit and float data stay that way), never through an 8-bit copy.
/// </summary>
public sealed class BitmapRequest
{
    /// <summary>EXIF orientation and vertical flip. Default <see cref="BitmapOrientation.FromImage"/>.</summary>
    public BitmapOrientation Orientation { get; init; }

    /// <summary>Alpha premultiplication. Default <see cref="BitmapPremultiply.Default"/> (premultiplied).</summary>
    public BitmapPremultiply PremultiplyAlpha { get; init; }

    /// <summary>Color space conversion. Default <see cref="BitmapColorSpaceConversion.Default"/>.</summary>
    public BitmapColorSpaceConversion ColorSpaceConversion { get; init; }

    /// <summary>The color space of the result. Default <see cref="NamedColorSpace.Srgb"/>.</summary>
    public NamedColorSpace TargetColorSpace { get; init; } = NamedColorSpace.Srgb;

    /// <summary>The region to keep, in pixels of the oriented image; <see langword="null"/> keeps everything. Applied before the resize.</summary>
    public ImageRegion? Crop { get; init; }

    /// <summary>The width to resize to; with only one of <see cref="ResizeWidth"/> and <see cref="ResizeHeight"/> set the other keeps the aspect ratio.</summary>
    public int? ResizeWidth { get; init; }

    /// <summary>The height to resize to; see <see cref="ResizeWidth"/>.</summary>
    public int? ResizeHeight { get; init; }

    /// <summary>The resampling quality. Default <see cref="ResizeQuality.Low"/>.</summary>
    public ResizeQuality ResizeQuality { get; init; } = ResizeQuality.Low;

    /// <summary>
    /// The pixel format of the result, overriding the choice made from the decoded format and <see cref="PremultiplyAlpha"/> (for example
    /// <see cref="PixelFormat.PremultipliedRgbaF16"/> for a float canvas). <see langword="null"/> keeps the decoded sample type.
    /// </summary>
    public PixelFormat? OutputFormat { get; init; }

    /// <summary>Refuse images with more pixels than this (see <see cref="DecoderOptions.MaxPixels"/>).</summary>
    public long? MaxPixels { get; init; }

    /// <summary>The format names that may be decoded (see <see cref="DecoderOptions.AllowedFormats"/>).</summary>
    public IReadOnlySet<string>? AllowedFormats { get; init; }

    /// <summary>The most threads decode and processing may use; -1 leaves it to the library.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>An optional scheduler for the parallel work.</summary>
    public TaskScheduler? TaskScheduler { get; init; }

    /// <summary>
    /// What to do with an HDR (PQ or HLG) image. <see langword="null"/> (the default) tone-maps to SDR unless the result is floating point (the
    /// image already was, or <see cref="OutputFormat"/> is), in which case the headroom is kept. See
    /// <see cref="PeachImage.HdrToneMapping"/> for the two behaviors; kept headroom is carried in a float working format with 1.0 as SDR reference white (203 cd/m2).
    /// </summary>
    public HdrToneMapping? HdrToneMapping { get; init; }

    /// <summary>The peak luminance in cd/m2 of HDR content, which tone mapping maps to white. Default 1000.</summary>
    public double HdrPeakNits { get; init; } = 1000;
}
