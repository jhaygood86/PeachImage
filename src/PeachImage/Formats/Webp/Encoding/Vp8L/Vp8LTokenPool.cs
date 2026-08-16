using System.Buffers;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// A dedicated <see cref="ArrayPool{T}"/> for the tokenizer's per-image <see cref="Vp8LToken"/> buffer.
/// Mirrors <see cref="Internal.WebpBufferPool"/>'s reasoning for its own oversized-array pools (default
/// <see cref="ArrayPool{T}.Shared"/>'s pooled-length ceiling is far smaller than a single large image's token
/// buffer, so renting from it would silently fall back to a fresh, unpooled allocation every call) -- kept
/// separate from that pool rather than added to it since <see cref="Vp8LToken"/> is an encode-only type and
/// <see cref="Internal.WebpBufferPool"/> is meant to stay a low-level utility with no dependency back on the
/// encode/decode layers built on top of it.
/// </summary>
internal static class Vp8LTokenPool
{
    private const int MaxArrayLength = 8 * 1024 * 1024;
    private const int MaxArraysPerBucket = 4;

    public static ArrayPool<Vp8LToken> Shared { get; } = ArrayPool<Vp8LToken>.Create(MaxArrayLength, MaxArraysPerBucket);
}
