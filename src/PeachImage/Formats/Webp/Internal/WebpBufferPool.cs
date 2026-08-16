using System.Buffers;

namespace PeachImage.Formats.Webp.Internal;

/// <summary>
/// A dedicated <see cref="ArrayPool{T}"/> for WebP decode's large per-image working buffers (VP8L's flat
/// ARGB pixel buffer, ALPH's lossless-substream working buffer). <see cref="ArrayPool{T}.Shared"/>'s default
/// max pooled array length is 1 MiB — smaller than a single image's ARGB buffer for anything above roughly
/// 512x512 pixels — so renting oversized buffers from it silently falls back to a fresh heap allocation every
/// call, defeating the point of pooling for exactly the large-image case that benefits most. This pool raises
/// that ceiling to comfortably cover realistic large WebP images. Mirrors <c>Gif.Internal.GifBufferPool</c>.
/// </summary>
internal static class WebpBufferPool
{
    private const int MaxArrayLength = 32 * 1024 * 1024;
    private const int MaxArraysPerBucket = 4;

    public static ArrayPool<byte> Shared { get; } = ArrayPool<byte>.Create(MaxArrayLength, MaxArraysPerBucket);

    /// <summary>
    /// A separate pool for VP8L's flat <c>uint[]</c> ARGB working buffer (one element per pixel). Kept
    /// distinct from <see cref="Shared"/> (which pools <c>byte[]</c>) rather than reinterpreting a rented
    /// byte buffer via <c>MemoryMarshal.Cast</c>, since every consumer of the ARGB buffer already indexes it
    /// as <c>uint[]</c> throughout — a separate element-typed pool avoids threading cast/reinterpret calls
    /// through the whole VP8L pixel/transform pipeline for no benefit. Sized in elements (not bytes): 8M
    /// elements = 32 MiB, matching <see cref="Shared"/>'s byte-oriented cap for consistency.
    /// </summary>
    public static ArrayPool<uint> SharedUInt32 { get; } = ArrayPool<uint>.Create(8 * 1024 * 1024, MaxArraysPerBucket);

    /// <summary>
    /// A separate pool for encode-only <c>int[]</c> scratch buffers -- currently <c>Vp8LMatchFinder</c>'s hash
    /// table and hash-chain arrays, both sized to (or a small multiple of) the pixel count and discarded after
    /// one use per image, the same large-transient-allocation pattern <see cref="SharedUInt32"/> exists for on
    /// the decode side.
    /// </summary>
    public static ArrayPool<int> SharedInt32 { get; } = ArrayPool<int>.Create(8 * 1024 * 1024, MaxArraysPerBucket);
}
