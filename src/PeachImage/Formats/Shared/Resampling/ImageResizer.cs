using System.Buffers;
using System.Runtime.InteropServices;

namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Resizes an <see cref="Image"/> to new dimensions using a selectable <see cref="ResamplingFilter"/>. Every
/// non-<see cref="ResamplingFilter.NearestNeighbor"/> filter runs the same generic separable two-pass
/// convolution regardless of <see cref="PixelFormat"/> — one algorithm parameterized by channel count and
/// sample width, not one per format.
/// </summary>
internal static class ImageResizer
{
    /// <summary>
    /// Every pixel in the intermediate float buffers this resizer convolves occupies exactly this many
    /// floats, whatever the source <see cref="PixelFormat"/>'s real channel count — unused channels are
    /// simply zero and ignored on the way back out. This fixed width lets both convolution passes
    /// (<see cref="IResamplingConvolver"/>) use one unconditional Vector128-per-pixel SIMD path with no
    /// per-channel-count branching or partial-vector edge cases.
    /// </summary>
    internal const int FloatsPerPixel = 4;

    public static Image Resize(Image source, int width, int height, ResamplingFilter filter)
    {
        if (filter == ResamplingFilter.NearestNeighbor)
        {
            return ResizeNearestNeighbor(source, width, height);
        }

        var kernel = ResamplingKernelFactory.Create(filter);
        var horizontalWeights = new ResamplingWeightMap(source.Width, width, kernel);
        var verticalWeights = new ResamplingWeightMap(source.Height, height, kernel);
        return ResizeWithWeights(source, width, height, horizontalWeights, verticalWeights);
    }

    /// <summary>
    /// Resizes using precomputed <paramref name="horizontalWeights"/>/<paramref name="verticalWeights"/> —
    /// for callers (like <see cref="AnimatedImage.Resize"/>) that resize many same-size, same-filter frames
    /// and want to build the weight maps once rather than rebuild them from scratch on every frame.
    /// <paramref name="horizontalWeights"/> and <paramref name="verticalWeights"/> must already have been
    /// built from <paramref name="source"/>'s actual width/height — not validated here, since a mismatch
    /// would corrupt output silently rather than throw; it's the caller's responsibility to only reuse a
    /// weight map across sources that genuinely share the same dimensions (e.g. every frame of one
    /// <see cref="AnimatedImage"/>, which by construction share its canvas size). Not used for
    /// <see cref="ResamplingFilter.NearestNeighbor"/>, which has no weight map — see <see cref="Resize"/>.
    /// </summary>
    public static Image ResizeWithWeights(Image source, int width, int height, ResamplingWeightMap horizontalWeights, ResamplingWeightMap verticalWeights)
    {
        bool hasAlpha = source.PixelFormat is PixelFormat.Rgba32 or PixelFormat.Rgba64;
        int channelCount = source.PixelFormat.GetChannelCount();
        bool is16Bit = source.PixelFormat.GetBytesPerSample() == 2;

        int sourceFloatCount = source.Width * source.Height * FloatsPerPixel;
        int resizedFloatCount = width * height * FloatsPerPixel;

        // The two pass orders (horizontal-then-vertical, or vertical-then-horizontal) reach the same
        // destination and are equally correct — only their intermediate buffer's pixel count differs.
        // Running whichever axis produces the smaller intermediate first means the second pass (whose cost
        // scales with the final width x height regardless of order) processes a smaller carried-over buffer,
        // so this is a free win on any resize whose two axes scale by meaningfully different amounts.
        bool horizontalFirst = (long)width * source.Height <= (long)source.Width * height;
        int intermediateFloatCount = (horizontalFirst ? width * source.Height : source.Width * height) * FloatsPerPixel;

        var pool = ArrayPool<float>.Shared;
        float[] sourceBuffer = pool.Rent(sourceFloatCount);
        float[] intermediate = pool.Rent(intermediateFloatCount);
        float[] resized = pool.Rent(resizedFloatCount);
        try
        {
            // Every buffer below may be a larger-than-requested ArrayPool rental; each call is bounded by
            // its own width/height/DestinationSize parameters, never by the array's Length, so passing the
            // raw rented array (rather than an exact-length slice) is safe — see IResamplingConvolver's
            // remarks for why these are plain arrays (not Span<float>) in the first place: it's what lets
            // each convolver parallelize its per-row loop, since a Span can't be captured by that closure.
            ToFloatBuffer(source, is16Bit, hasAlpha, sourceBuffer);

            var convolver = ResamplingConvolverSelector.Instance;
            if (horizontalFirst)
            {
                convolver.ConvolveHorizontal(sourceBuffer, source.Width, source.Height, intermediate, horizontalWeights);
                convolver.ConvolveVertical(intermediate, width, source.Height, resized, verticalWeights);
            }
            else
            {
                convolver.ConvolveVertical(sourceBuffer, source.Width, source.Height, intermediate, verticalWeights);
                convolver.ConvolveHorizontal(intermediate, source.Width, height, resized, horizontalWeights);
            }

            return FromFloatBuffer(resized, width, height, source.PixelFormat, channelCount, is16Bit, hasAlpha);
        }
        finally
        {
            pool.Return(sourceBuffer);
            pool.Return(intermediate);
            pool.Return(resized);
        }
    }

    private static Image ResizeNearestNeighbor(Image source, int width, int height)
    {
        var destination = Image.Create(width, height, source.PixelFormat);
        int bytesPerPixel = source.PixelFormat.GetBytesPerPixel();

        for (int y = 0; y < height; y++)
        {
            int srcY = MapNearestIndex(y, height, source.Height);
            var srcRow = source.GetRowSpan(srcY);
            var destRow = destination.GetRowSpan(y);

            for (int x = 0; x < width; x++)
            {
                int srcX = MapNearestIndex(x, width, source.Width);
                srcRow.Slice(srcX * bytesPerPixel, bytesPerPixel).CopyTo(destRow.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }

        return destination;
    }

    private static int MapNearestIndex(int destIndex, int destSize, int sourceSize)
    {
        double sourcePosition = (destIndex + 0.5) * sourceSize / destSize;
        return Math.Clamp((int)sourcePosition, 0, sourceSize - 1);
    }

    /// <summary>
    /// Widens every source pixel to <see cref="FloatsPerPixel"/> floats, written into <paramref name="buffer"/>
    /// (caller-owned, e.g. rented from <see cref="ArrayPool{T}"/>). For <paramref name="hasAlpha"/> formats,
    /// RGB is premultiplied by alpha here (and un-premultiplied in <see cref="FromFloatBuffer"/>) so fully or
    /// partially transparent pixels' color doesn't bleed into opaque edges during convolution.
    /// </summary>
    /// <remarks>
    /// For non-<paramref name="hasAlpha"/> formats with fewer than <see cref="FloatsPerPixel"/> real
    /// channels (Gray8/16: 1, Rgb24/48: 3), the unused trailing lanes are deliberately left unwritten —
    /// including when <paramref name="buffer"/> is a reused <see cref="ArrayPool{T}"/> rental that still
    /// holds a previous caller's data. This is safe, not a bug: every convolution pass processes all 4 lanes
    /// per pixel independently (no cross-lane math), and <see cref="FromFloatBuffer"/> only ever reads back
    /// the first <c>channelCount</c> lanes — so whatever garbage rides along in the unused lanes is computed
    /// on, then discarded, and never observed.
    /// </remarks>
    /// <remarks>
    /// Runs per source row through <see cref="ResamplingParallel"/>, the same as both convolution passes.
    /// <paramref name="buffer"/> is a plain array (not <see cref="Span{T}"/>) and <see cref="Image.PixelMemory"/>
    /// (not <see cref="Image.GetPixelSpan"/>) specifically so both can be captured by that closure — see
    /// <see cref="IResamplingConvolver"/>'s remarks for why <see cref="Span{T}"/> can't be.
    /// </remarks>
    private static void ToFloatBuffer(Image source, bool is16Bit, bool hasAlpha, float[] buffer)
    {
        var pixelMemory = source.PixelMemory;
        int bytesPerPixel = source.PixelFormat.GetBytesPerPixel();
        int channelCount = source.PixelFormat.GetChannelCount();
        int width = source.Width;
        int rowBytes = width * bytesPerPixel;
        int rowFloats = width * FloatsPerPixel;

        ResamplingParallel.For(source.Height, y =>
        {
            var rowPixels = pixelMemory.Span.Slice(y * rowBytes, rowBytes);
            var rowBuffer = buffer.AsSpan(y * rowFloats, rowFloats);

            for (int x = 0; x < width; x++)
            {
                int bufferOffset = x * FloatsPerPixel;
                var pixelBytes = rowPixels.Slice(x * bytesPerPixel, bytesPerPixel);

                if (is16Bit)
                {
                    WritePixel(rowBuffer, bufferOffset, MemoryMarshal.Cast<byte, ushort>(pixelBytes), channelCount, hasAlpha, fullScale: 65535f);
                }
                else
                {
                    WritePixel(rowBuffer, bufferOffset, pixelBytes, channelCount, hasAlpha, fullScale: 255f);
                }
            }
        });
    }

    private static void WritePixel(Span<float> buffer, int offset, ReadOnlySpan<byte> samples, int channelCount, bool hasAlpha, float fullScale)
    {
        if (hasAlpha)
        {
            WritePremultiplied(buffer, offset, samples[0], samples[1], samples[2], samples[3], fullScale);
            return;
        }

        for (int c = 0; c < channelCount; c++)
        {
            buffer[offset + c] = samples[c];
        }
    }

    private static void WritePixel(Span<float> buffer, int offset, ReadOnlySpan<ushort> samples, int channelCount, bool hasAlpha, float fullScale)
    {
        if (hasAlpha)
        {
            WritePremultiplied(buffer, offset, samples[0], samples[1], samples[2], samples[3], fullScale);
            return;
        }

        for (int c = 0; c < channelCount; c++)
        {
            buffer[offset + c] = samples[c];
        }
    }

    private static void WritePremultiplied(Span<float> buffer, int offset, float r, float g, float b, float alpha, float fullScale)
    {
        float alphaFraction = alpha / fullScale;
        buffer[offset] = r * alphaFraction;
        buffer[offset + 1] = g * alphaFraction;
        buffer[offset + 2] = b * alphaFraction;
        buffer[offset + 3] = alpha;
    }

    /// <summary>
    /// Narrows the resized float buffer back to <paramref name="format"/>'s native sample width,
    /// un-premultiplying alpha where <see cref="ToFloatBuffer"/> premultiplied it. Runs per destination row
    /// through <see cref="ResamplingParallel"/>, same as <see cref="ToFloatBuffer"/> and both convolution
    /// passes, and for the same reason takes <paramref name="buffer"/> as a plain array and writes through
    /// <see cref="Image.PixelMemory"/> rather than <see cref="Image.GetPixelSpan"/>.
    /// </summary>
    private static Image FromFloatBuffer(float[] buffer, int width, int height, PixelFormat format, int channelCount, bool is16Bit, bool hasAlpha)
    {
        var destination = Image.Create(width, height, format);
        var pixelMemory = destination.PixelMemory;
        int bytesPerPixel = format.GetBytesPerPixel();
        float fullScale = is16Bit ? 65535f : 255f;
        int rowBytes = width * bytesPerPixel;
        int rowFloats = width * FloatsPerPixel;

        ResamplingParallel.For(height, y =>
        {
            Span<float> channels = stackalloc float[FloatsPerPixel];
            var rowBuffer = buffer.AsSpan(y * rowFloats, rowFloats);
            var rowPixels = pixelMemory.Span.Slice(y * rowBytes, rowBytes);

            for (int x = 0; x < width; x++)
            {
                rowBuffer.Slice(x * FloatsPerPixel, FloatsPerPixel).CopyTo(channels);

                if (hasAlpha)
                {
                    UnpremultiplyInPlace(channels, fullScale);
                }

                var pixelBytes = rowPixels.Slice(x * bytesPerPixel, bytesPerPixel);
                if (is16Bit)
                {
                    var samples = MemoryMarshal.Cast<byte, ushort>(pixelBytes);
                    for (int c = 0; c < channelCount; c++)
                    {
                        samples[c] = (ushort)Math.Clamp(MathF.Round(channels[c]), 0f, fullScale);
                    }
                }
                else
                {
                    for (int c = 0; c < channelCount; c++)
                    {
                        pixelBytes[c] = (byte)Math.Clamp(MathF.Round(channels[c]), 0f, fullScale);
                    }
                }
            }
        });

        return destination;
    }

    private static void UnpremultiplyInPlace(Span<float> channels, float fullScale)
    {
        float alpha = Math.Clamp(channels[3], 0f, fullScale);
        float unpremultiplyScale = alpha > 0f ? fullScale / alpha : 0f;
        channels[0] = Math.Clamp(channels[0] * unpremultiplyScale, 0f, fullScale);
        channels[1] = Math.Clamp(channels[1] * unpremultiplyScale, 0f, fullScale);
        channels[2] = Math.Clamp(channels[2] * unpremultiplyScale, 0f, fullScale);
        channels[3] = alpha;
    }
}
