using System.Buffers;

namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// A dedicated <see cref="ArrayPool{T}"/> for <see cref="ImageResizer"/>'s intermediate float buffers
/// (<see cref="ImageResizer.FloatsPerPixel"/> floats per pixel — 16 bytes/pixel, several times a
/// decoded image's own pixel buffer size). <see cref="ArrayPool{T}.Shared"/>'s default ~1 MiB max
/// pooled array length is far below a full-resolution photo's buffer here, so reusing it would
/// silently never pool anything at realistic photo sizes — mirrors <c>Internal.ImageBufferPool</c>'s
/// reasoning for the pixel buffer itself.
/// </summary>
internal static class ResamplingBufferPool
{
    private const int MaxArrayLength = 128 * 1024 * 1024;
    private const int MaxArraysPerBucket = 2;

    public static ArrayPool<float> Shared { get; } = ArrayPool<float>.Create(MaxArrayLength, MaxArraysPerBucket);
}
