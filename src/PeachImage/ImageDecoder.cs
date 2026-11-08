namespace PeachImage;

/// <summary>
/// Decodes images into memory the caller owns, so a hot loop (a thumbnailer, a tile server) decodes into one reusable buffer
/// instead of getting a fresh <see cref="Image"/> per call. Create one per thread; an instance is not safe for concurrent use.
/// </summary>
/// <remarks>
/// <para>
/// The destination holds the decoded pixels in the format the image decodes to, or <see cref="DecoderOptions.TargetPixelFormat"/> when
/// set; the returned <see cref="ImageInfo"/> says which. <see cref="DecoderOptions.ScaleDenominator"/>, <see cref="DecoderOptions.TargetSize"/>,
/// <see cref="DecoderOptions.ApplyOrientation"/> and the size limits behave as they do for <see cref="Image.Load(Stream, DecoderOptions?)"/>, and
/// <see cref="ImageInfo.GetRequiredBufferSize"/> accounts for them. An animated source decodes its first frame.
/// </para>
/// <para>
/// Metadata comes back through the <c>metadata</c> parameter. With <see cref="DecoderOptions.SkipMetadata"/> set it is empty and none is parsed.
/// </para>
/// </remarks>
public sealed class ImageDecoder : IDisposable
{
    private readonly DecoderOptions? _options;
    private bool _disposed;

    /// <summary>Creates a decoder that applies <paramref name="options"/> to every call.</summary>
    public ImageDecoder(DecoderOptions? options = null)
    {
        _options = options;
    }

    /// <summary>
    /// Decodes <paramref name="data"/> into <paramref name="destination"/>, rows <paramref name="rowStride"/> bytes apart (0, the default,
    /// packs them tightly).
    /// </summary>
    /// <returns>The decoded image's description: its size and pixel format as written to <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="ImageInfo.GetRequiredBufferSize"/>, or <paramref name="rowStride"/> is less than one row.</exception>
    /// <exception cref="ObjectDisposedException">This decoder has been disposed.</exception>
    public ImageInfo DecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, out ImageMetadata metadata, int rowStride = 0)
    {
        ThrowIfDisposed();
        return CallerBufferDecode.RunSpanOrThrow(data, _options, destination, rowStride, out metadata);
    }

    /// <summary>Decodes the image at <paramref name="stream"/>'s position into <paramref name="destination"/>; see <see cref="DecodeInto(ReadOnlySpan{byte}, Span{byte}, out ImageMetadata, int)"/>.</summary>
    public ImageInfo DecodeInto(Stream stream, Span<byte> destination, out ImageMetadata metadata, int rowStride = 0)
    {
        ThrowIfDisposed();
        return CallerBufferDecode.RunOrThrow(stream, _options, destination, rowStride, out metadata);
    }

    /// <summary>
    /// Like <see cref="DecodeInto(ReadOnlySpan{byte}, Span{byte}, out ImageMetadata, int)"/>, but returns <see langword="false"/> instead of throwing
    /// when <paramref name="destination"/> is too small. Nothing is decoded then, <paramref name="info"/> is the header's own description
    /// (so <c>info.GetRequiredBufferSize(options, rowStride)</c> gives the size needed) and <paramref name="metadata"/> is empty. A damaged
    /// or unsupported image still throws.
    /// </summary>
    public bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, out ImageInfo info, out ImageMetadata metadata, int rowStride = 0)
    {
        ThrowIfDisposed();
        return CallerBufferDecode.RunSpan(data, _options, destination, rowStride, out info, out metadata, out _);
    }

    /// <summary>Releases the decoder; further calls throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose() => _disposed = true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
