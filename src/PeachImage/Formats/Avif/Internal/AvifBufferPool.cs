using System.Buffers;

namespace PeachImage.Formats.Avif.Internal;

/// <summary>
/// A dedicated <see cref="ArrayPool{T}"/> for AVIF decode's large per-plane working buffers, mirroring
/// <c>Webp.Internal.WebpBufferPool</c>. Raises <see cref="ArrayPool{T}.Shared"/>'s default 1 MiB max
/// pooled array length, which would otherwise silently fall back to a fresh heap allocation for any AV1
/// plane above roughly 512x512 pixels — the exact large-image case pooling is meant to help most. Not yet
/// used (no pixel decode exists until Phase 2+); scaffolded now so later phases have a shared pool to rent
/// from from the start rather than introducing ad hoc pooling per component.
/// </summary>
internal static class AvifBufferPool
{
    private const int MaxArrayLength = 64 * 1024 * 1024;
    private const int MaxArraysPerBucket = 4;

    /// <summary>Pool for 8-bit plane data and general byte-oriented scratch buffers.</summary>
    public static ArrayPool<byte> Shared { get; } = ArrayPool<byte>.Create(MaxArrayLength, MaxArraysPerBucket);

    /// <summary>A separate pool for 10-bit plane data, stored natively as <c>ushort[]</c> rather than a reinterpreted <c>byte[]</c>.</summary>
    public static ArrayPool<ushort> SharedUInt16 { get; } = ArrayPool<ushort>.Create(32 * 1024 * 1024, MaxArraysPerBucket);

    /// <summary>
    /// A separate pool for the AV1 encoder's <c>int[]</c> working buffers -- per-tile block-scratch (prediction,
    /// residual, coefficient, and dequantization buffers, rented once per <c>Av1TileEncoder.EncodeTile</c> call
    /// and reused across every block rather than allocated fresh per block) and per-frame plane buffers (YUV
    /// conversion, padding, reconstruction). Mirrors <c>Webp.Internal.WebpBufferPool.SharedInt32</c>'s exact
    /// rationale for encode-only <c>int[]</c> scratch.
    /// </summary>
    public static ArrayPool<int> SharedInt32 { get; } = ArrayPool<int>.Create(MaxArrayLength / sizeof(int), MaxArraysPerBucket);
}
