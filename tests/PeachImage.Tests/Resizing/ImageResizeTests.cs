using PeachImage.Formats.Shared.Resampling;

namespace PeachImage.Tests.Resizing;

public class ImageResizeTests
{
    public static IEnumerable<object[]> AllPixelFormats()
    {
        foreach (PixelFormat format in Enum.GetValues<PixelFormat>())
        {
            yield return [format];
        }
    }

    // Filters whose (B, C) or window function reproduces the original sample exactly at every integer
    // offset (i.e. weight is 1 at offset 0 and 0 at every other integer within the radius) — resizing to
    // the SAME dimensions should therefore be an exact, lossless round trip for these, byte-for-byte.
    public static IEnumerable<object[]> InterpolatingFilters()
    {
        foreach (var filter in new[]
        {
            ResamplingFilter.Bicubic, ResamplingFilter.CatmullRom, ResamplingFilter.Hermite,
            ResamplingFilter.Box, ResamplingFilter.Bilinear, ResamplingFilter.Welch, ResamplingFilter.NearestNeighbor,
            ResamplingFilter.Lanczos2, ResamplingFilter.Lanczos3, ResamplingFilter.Lanczos5, ResamplingFilter.Lanczos8,
        })
        {
            yield return [filter];
        }
    }

    [Theory]
    [MemberData(nameof(AllPixelFormats))]
    public void Resize_ProducesCorrectDimensions_ForEveryPixelFormat(PixelFormat format)
    {
        var source = Image.Create(9, 7, format);
        FillWithRandomBytes(source);

        var downscaled = source.Resize(3, 2, new ResizeOptions { Filter = ResamplingFilter.Bicubic });
        Assert.Equal(3, downscaled.Width);
        Assert.Equal(2, downscaled.Height);
        Assert.Equal(format, downscaled.PixelFormat);

        var upscaled = source.Resize(20, 15, new ResizeOptions { Filter = ResamplingFilter.Bicubic });
        Assert.Equal(20, upscaled.Width);
        Assert.Equal(15, upscaled.Height);
        Assert.Equal(format, upscaled.PixelFormat);
    }

    [Fact]
    public void Resize_WithoutOptions_DefaultsToBicubic()
    {
        var source = Image.Create(9, 7, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var defaultResized = source.Resize(4, 3);
        var explicitBicubic = source.Resize(4, 3, new ResizeOptions { Filter = ResamplingFilter.Bicubic });

        Assert.Equal(explicitBicubic.GetPixelSpan().ToArray(), defaultResized.GetPixelSpan().ToArray());
    }

    [Theory]
    [MemberData(nameof(InterpolatingFilters))]
    public void SameSizeResize_IsExactByteIdentity_ForInterpolatingFilters(ResamplingFilter filter)
    {
        var source = Image.Create(11, 8, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var resized = source.Resize(11, 8, new ResizeOptions { Filter = filter });

        Assert.Equal(source.GetPixelSpan().ToArray(), resized.GetPixelSpan().ToArray());
    }

    [Theory]
    [MemberData(nameof(InterpolatingFilters))]
    public void SameSizeResize_IsExactByteIdentity_ForInterpolatingFilters_AboveParallelThreshold(ResamplingFilter filter)
    {
        // Every other test in this file uses small images that stay under ResamplingParallel's
        // MinRowsForParallel (64) on both axes, so they only ever exercise the sequential fallback loop —
        // this one is sized to push both convolution passes' per-row loop through the actual
        // Parallel.For(...) branch instead, to catch a thread-safety bug the sequential path can't reveal.
        var source = Image.Create(80, 96, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var resized = source.Resize(80, 96, new ResizeOptions { Filter = filter });

        Assert.Equal(source.GetPixelSpan().ToArray(), resized.GetPixelSpan().ToArray());
    }

    [Theory]
    [InlineData(256, 256, 240, 40)] // destWidth*sourceHeight (61440) > sourceWidth*destHeight (10240): ImageResizer picks vertical-first.
    [InlineData(256, 256, 40, 240)] // the transposed shape: destWidth*sourceHeight (10240) <= sourceWidth*destHeight (61440), picks horizontal-first.
    public void Resize_MatchesIndependentReference_RegardlessOfWhichPassOrderIsChosen(int sourceWidth, int sourceHeight, int destWidth, int destHeight)
    {
        // ImageResizer.ResizeWithWeights picks whichever pass order produces the smaller intermediate
        // buffer (see its remarks) — these two cases are shaped to force each order in turn. The reference
        // below always computes horizontal-then-vertical in double precision, independent of production
        // code and regardless of which order Image.Resize actually took, so a mismatch here would mean the
        // vertical-first branch's buffer/dimension bookkeeping is wrong, not just "different by rounding".
        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgb24);
        FillWithRandomBytes(source);

        var resized = source.Resize(destWidth, destHeight, new ResizeOptions { Filter = ResamplingFilter.Bicubic });
        byte[] reference = ReferenceResizeRgb24(source, destWidth, destHeight, ResamplingFilter.Bicubic);

        var actual = resized.GetPixelSpan().ToArray();
        Assert.Equal(reference.Length, actual.Length);
        for (int i = 0; i < reference.Length; i++)
        {
            Assert.True(Math.Abs(reference[i] - actual[i]) <= 1, $"index {i}: reference {reference[i]}, actual {actual[i]}");
        }
    }

    /// <summary>Independent double-precision horizontal-then-vertical reference resize for Rgb24 (no alpha), not a call into any production convolver.</summary>
    private static byte[] ReferenceResizeRgb24(Image source, int width, int height, ResamplingFilter filter)
    {
        const int channels = 3;
        var kernel = ResamplingKernelFactory.Create(filter);
        var horizontalMap = new ResamplingWeightMap(source.Width, width, kernel);
        var verticalMap = new ResamplingWeightMap(source.Height, height, kernel);
        var src = source.GetPixelSpan();

        var horizontallyResized = new double[width * source.Height * channels];
        for (int y = 0; y < source.Height; y++)
        {
            for (int destX = 0; destX < width; destX++)
            {
                var weights = horizontalMap.GetWeights(destX);
                int start = horizontalMap.Starts[destX];
                for (int c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < weights.Length; k++)
                    {
                        sum += src[(((y * source.Width) + start + k) * channels) + c] * (double)weights[k];
                    }

                    horizontallyResized[(((y * width) + destX) * channels) + c] = sum;
                }
            }
        }

        var result = new byte[width * height * channels];
        for (int destY = 0; destY < height; destY++)
        {
            var weights = verticalMap.GetWeights(destY);
            int start = verticalMap.Starts[destY];
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < weights.Length; k++)
                    {
                        sum += horizontallyResized[((((start + k) * width) + x) * channels) + c] * (double)weights[k];
                    }

                    result[(((destY * width) + x) * channels) + c] = (byte)Math.Clamp(Math.Round(sum), 0, 255);
                }
            }
        }

        return result;
    }

    [Fact]
    public void NearestNeighbor_EveryOutputPixel_ExactlyMatchesItsMappedSourcePixel()
    {
        const int sourceWidth = 10;
        const int sourceHeight = 6;
        const int destWidth = 4;
        const int destHeight = 9;

        var source = Image.Create(sourceWidth, sourceHeight, PixelFormat.Rgba32);
        FillWithRandomBytes(source);

        var resized = source.Resize(destWidth, destHeight, new ResizeOptions { Filter = ResamplingFilter.NearestNeighbor });
        var sourceSpan = source.GetPixelSpan();
        var destSpan = resized.GetPixelSpan();

        for (int y = 0; y < destHeight; y++)
        {
            int srcY = Math.Clamp((int)((y + 0.5) * sourceHeight / (double)destHeight), 0, sourceHeight - 1);
            for (int x = 0; x < destWidth; x++)
            {
                int srcX = Math.Clamp((int)((x + 0.5) * sourceWidth / (double)destWidth), 0, sourceWidth - 1);

                int srcOffset = ((srcY * sourceWidth) + srcX) * 4;
                int destOffset = ((y * destWidth) + x) * 4;
                Assert.Equal(sourceSpan.Slice(srcOffset, 4).ToArray(), destSpan.Slice(destOffset, 4).ToArray());
            }
        }
    }

    [Fact]
    public void Rgba32_TransparentEdge_DoesNotBleedIntoOpaqueColor()
    {
        // Fully transparent black next to fully opaque white: without premultiplying by alpha before
        // convolving, the transparent pixel's black RGB would bleed into the white edge on upscale.
        var source = Image.Create(2, 1, PixelFormat.Rgba32);
        var span = source.GetPixelSpan();
        span[0] = 0;
        span[1] = 0;
        span[2] = 0;
        span[3] = 0; // transparent black
        span[4] = 255;
        span[5] = 255;
        span[6] = 255;
        span[7] = 255; // opaque white

        var upscaled = source.Resize(8, 1, new ResizeOptions { Filter = ResamplingFilter.Bilinear });
        var result = upscaled.GetPixelSpan();

        for (int x = 0; x < 8; x++)
        {
            int o = x * 4;
            byte alpha = result[o + 3];
            if (alpha == 0)
            {
                continue; // fully transparent pixels carry no visible color regardless of their RGB.
            }

            // Un-premultiplied RGB should stay near white (255) at every visible alpha level, never dip
            // toward black the way it would if transparent black's RGB had bled in before premultiplying.
            Assert.True(result[o] >= 250, $"x={x}: R={result[o]}, alpha={alpha}");
            Assert.True(result[o + 1] >= 250, $"x={x}: G={result[o + 1]}, alpha={alpha}");
            Assert.True(result[o + 2] >= 250, $"x={x}: B={result[o + 2]}, alpha={alpha}");
        }
    }

    [Fact]
    public void RepeatedResizes_AcrossPixelFormats_DoNotLeakStaleArrayPoolData()
    {
        // ImageResizer rents its intermediate float buffers from ArrayPool<float>.Shared rather than
        // allocating fresh arrays — interleaving formats with fewer than 4 real channels (Gray8: 1, Rgb24:
        // 3) against Rgba32 (which writes all 4 lanes, including what's "padding" for the others) maximizes
        // the chance a reused rental still carries a previous call's data in an unused lane. That's expected
        // to be harmless (see ImageResizer.ToFloatBuffer's remarks: unused lanes are computed on but never
        // read back), and this asserts it actually is, empirically, across many rent/return cycles rather
        // than trusting the reasoning alone.
        PixelFormat[] formats = [PixelFormat.Gray8, PixelFormat.Rgba32, PixelFormat.Rgb24, PixelFormat.Rgba32, PixelFormat.Gray8];

        for (int iteration = 0; iteration < 20; iteration++)
        {
            foreach (var format in formats)
            {
                var source = Image.Create(11, 8, format);
                FillWithRandomBytes(source);

                // Interpolating filter at the same size: exact byte identity is the strongest possible
                // assertion here (see SameSizeResize_IsExactByteIdentity_ForInterpolatingFilters) — any
                // cross-lane leakage would show up as a wrong byte somewhere in the output.
                var resized = source.Resize(11, 8, new ResizeOptions { Filter = ResamplingFilter.Lanczos3 });
                Assert.Equal(source.GetPixelSpan().ToArray(), resized.GetPixelSpan().ToArray());
            }
        }
    }

    [Fact]
    public void Resize_ThrowsForNonPositiveDimensions()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb24);

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Resize(-1, 4));
    }

    private static void FillWithRandomBytes(Image image)
    {
        var random = new Random(image.Width * 31 + image.Height);
        var span = image.GetPixelSpan();
        random.NextBytes(span);
    }
}
