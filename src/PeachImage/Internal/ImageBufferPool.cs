using System.Buffers;

namespace PeachImage.Internal;

/// <summary>
/// A dedicated <see cref="ArrayPool{T}"/> for <see cref="Image"/>'s pixel buffers, mirroring the
/// per-format dedicated pools (e.g. <c>Webp.Internal.WebpBufferPool</c>). Raises
/// <see cref="ArrayPool{T}.Shared"/>'s default ~1 MiB max pooled array length, which would otherwise
/// silently fall back to a fresh heap allocation (on both rent and return) for any full-resolution
/// decoded photo — exactly the size class this pool exists to cover.
/// </summary>
internal static class ImageBufferPool
{
    private const int MaxArrayLength = 512 * 1024 * 1024;
    private const int MaxArraysPerBucket = 2;

    public static ArrayPool<byte> Shared { get; } = ArrayPool<byte>.Create(MaxArrayLength, MaxArraysPerBucket);
}
