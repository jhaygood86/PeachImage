using System.Buffers;

namespace PeachImage.Formats.Gif.Internal;

/// <summary>
/// A dedicated <see cref="ArrayPool{T}"/> for GIF decode's large per-frame working buffers (LZW image data,
/// decoded indices). <see cref="ArrayPool{T}.Shared"/>'s default max pooled array length is 1 MiB — smaller
/// than a single frame's indices buffer for anything above roughly 1024x1024 pixels (e.g. a 1920x1080 frame
/// needs ~2 MiB), so renting oversized buffers from it silently falls back to a fresh heap allocation every
/// call, defeating the point of pooling for exactly the large-image case that benefits most. This pool raises
/// that ceiling to comfortably cover realistic large GIFs.
/// </summary>
internal static class GifBufferPool
{
    private const int MaxArrayLength = 32 * 1024 * 1024;
    private const int MaxArraysPerBucket = 4;

    public static ArrayPool<byte> Shared { get; } = ArrayPool<byte>.Create(MaxArrayLength, MaxArraysPerBucket);
}
