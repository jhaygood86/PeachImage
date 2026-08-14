using System.Buffers;
using PeachImage.Formats.Jpeg.ColorConversion;
using PeachImage.Formats.Jpeg.Decoding.Upsampling;
using PeachImage.Formats.Jpeg.Dct;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Turns a fully-decoded <see cref="DecodedFrame"/> (coefficient buffers only) into pixels: dequantize + inverse
/// DCT each block into a per-component plane, upsample subsampled chroma planes to full resolution, then
/// color-convert into the final <see cref="Image"/>. Every intermediate per-component plane is rented from
/// <see cref="ArrayPool{T}"/> and returned once the frame is fully reconstructed — none of them are large
/// enough or long-lived enough to be worth a fresh heap allocation each decode, and at 1080p+ resolutions
/// several land on the large object heap, so pooling them measurably cuts GC pressure. Only the final,
/// tightly-sized buffer that becomes the returned <see cref="Image"/>'s backing store is a plain array.
/// </summary>
internal static class FrameReconstructor
{
    /// <summary>Reconstructs pixel data from <paramref name="frame"/>.</summary>
    public static Image Reconstruct(DecodedFrame frame, JpegDecoderOptions? options)
    {
        int width = frame.FrameHeader.Width;
        int height = frame.FrameHeader.Height;

        IChromaUpsampler upsampler = options?.FastUpsampling == true
            ? new NearestNeighborUpsampler()
            : new TriangleFilterUpsampler();

        int hMax = 1;
        int vMax = 1;
        foreach (var c in frame.Components)
        {
            hMax = Math.Max(hMax, c.Frame.HorizontalSamplingFactor);
            vMax = Math.Max(vMax, c.Frame.VerticalSamplingFactor);
        }

        var planes = new byte[frame.Components.Length][];
        var rented = new List<byte[]>(frame.Components.Length * 3);

        try
        {
            for (int i = 0; i < frame.Components.Length; i++)
            {
                planes[i] = ReconstructComponentPlane(frame.Components[i], width, height, hMax, vMax, upsampler, rented);
            }

            int pixelCount = width * height;
            return frame.ColorSpace switch
            {
                JpegColorSpace.Grayscale => Image.FromBuffer(width, height, PixelFormat.Gray8, planes[0].AsSpan(0, pixelCount).ToArray()),
                JpegColorSpace.YCbCr => BuildYCbCrImage(planes, width, height),
                JpegColorSpace.Rgb => BuildDirectRgbImage(planes, width, height),
                JpegColorSpace.Cmyk => BuildDirectCmykImage(planes, width, height, frame.IsAdobeInverted),
                JpegColorSpace.Ycck => BuildYcckImage(planes, width, height),
                _ => throw new JpegDecodingException($"Unsupported JPEG color space: {frame.ColorSpace}."),
            };
        }
        finally
        {
            foreach (var buffer in rented)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static byte[] ReconstructComponentPlane(ComponentDecodeState component, int width, int height, int hMax, int vMax, IChromaUpsampler upsampler, List<byte[]> rented)
    {
        int actualWidth = component.ActualWidthInSamples;
        int actualHeight = component.ActualHeightInSamples;
        var cropped = Rent(actualWidth * actualHeight, rented);

        var dequant = DctKernelSelector.Inverse.PrepareDequantTable(component.QuantizationTable.Values);

        // IDCT writes straight into `cropped` at its actual (non-MCU-padded) size — skipping the
        // MCU-padded-buffer-then-row-copy this used to do — by classifying each block against the
        // actual/padding boundary: an interior block (entirely within actualWidth/actualHeight) can take
        // outputStride = actualWidth directly, since both selected kernels (AanScalarInverseDct,
        // Vector256AanInverseDct — the only two DctKernelSelector.Inverse ever returns) write the full 8x8
        // unconditionally and an interior block's last written index never crosses a `cropped` row boundary.
        // A block straddling (or lying entirely past) that boundary — routine at any width/height that isn't
        // a multiple of 8 * samplingFactor, not just "the last block" — goes through a local 8x8 scratch
        // buffer first, and only the valid rows/columns are copied out; a block entirely past the boundary
        // (possible with sampling factors > 1) skips the transform altogether.
        Span<byte> scratch = stackalloc byte[64];
        for (int by = 0; by < component.Coefficients.BlocksHigh; by++)
        {
            int blockTop = by * 8;
            if (blockTop >= actualHeight)
            {
                break;
            }

            int rowsInBlock = Math.Min(8, actualHeight - blockTop);

            for (int bx = 0; bx < component.Coefficients.BlocksWide; bx++)
            {
                int blockLeft = bx * 8;
                if (blockLeft >= actualWidth)
                {
                    break;
                }

                var block = component.Coefficients.Block(bx, by);

                if (rowsInBlock == 8 && blockLeft + 8 <= actualWidth)
                {
                    int outOffset = (blockTop * actualWidth) + blockLeft;
                    DctKernelSelector.Inverse.Transform(block, dequant, cropped.AsSpan(outOffset), actualWidth);
                }
                else
                {
                    int colsInBlock = Math.Min(8, actualWidth - blockLeft);
                    DctKernelSelector.Inverse.Transform(block, dequant, scratch, outputStride: 8);
                    for (int y = 0; y < rowsInBlock; y++)
                    {
                        scratch.Slice(y * 8, colsInBlock).CopyTo(cropped.AsSpan(((blockTop + y) * actualWidth) + blockLeft, colsInBlock));
                    }
                }
            }
        }

        int hRatio = hMax / component.Frame.HorizontalSamplingFactor;
        int vRatio = vMax / component.Frame.VerticalSamplingFactor;

        if (hRatio == 1 && vRatio == 1)
        {
            return CropOrPad(cropped, actualWidth, actualHeight, width, height, rented);
        }

        int upsampledWidth = actualWidth * hRatio;
        int upsampledHeight = actualHeight * vRatio;
        var upsampled = Rent(upsampledWidth * upsampledHeight, rented);
        upsampler.Upsample(cropped, actualWidth, actualHeight, upsampled, upsampledWidth, upsampledHeight, hRatio, vRatio);

        return CropOrPad(upsampled, upsampledWidth, upsampledHeight, width, height, rented);
    }

    private static byte[] Rent(int size, List<byte[]> rented)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        rented.Add(buffer);
        return buffer;
    }

    private static byte[] CropOrPad(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, List<byte[]> rented)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
        {
            return source;
        }

        var result = Rent(targetWidth * targetHeight, rented);
        int copyWidth = Math.Min(sourceWidth, targetWidth);
        int copyHeight = Math.Min(sourceHeight, targetHeight);
        for (int y = 0; y < copyHeight; y++)
        {
            source.AsSpan(y * sourceWidth, copyWidth).CopyTo(result.AsSpan(y * targetWidth, copyWidth));
        }

        return result;
    }

    private static Image BuildYCbCrImage(byte[][] planes, int width, int height)
    {
        int pixelCount = width * height;
        var rgb = new byte[pixelCount * 3];
        ColorConverterSelector.Instance.YCbCrToRgb(planes[0], planes[1], planes[2], rgb, pixelCount);
        return Image.FromBuffer(width, height, PixelFormat.Rgb24, rgb);
    }

    private static Image BuildDirectRgbImage(byte[][] planes, int width, int height)
    {
        int pixelCount = width * height;
        var rgb = new byte[pixelCount * 3];
        InterleaveThree(planes[0], planes[1], planes[2], rgb, pixelCount);
        return Image.FromBuffer(width, height, PixelFormat.Rgb24, rgb);
    }

    private static Image BuildDirectCmykImage(byte[][] planes, int width, int height, bool isAdobeInverted)
    {
        int pixelCount = width * height;
        var cmyk = new byte[pixelCount * 4];
        InterleaveFour(planes[0], planes[1], planes[2], planes[3], cmyk, pixelCount);

        if (isAdobeInverted)
        {
            for (int i = 0; i < cmyk.Length; i++)
            {
                cmyk[i] = (byte)(255 - cmyk[i]);
            }
        }

        return Image.FromBuffer(width, height, PixelFormat.Cmyk32, cmyk);
    }

    private static Image BuildYcckImage(byte[][] planes, int width, int height)
    {
        int pixelCount = width * height;
        var cmyk = new byte[pixelCount * 4];
        ColorConverterSelector.Instance.YcckToCmyk(planes[0], planes[1], planes[2], planes[3], cmyk, pixelCount);
        return Image.FromBuffer(width, height, PixelFormat.Cmyk32, cmyk);
    }

    private static void InterleaveThree(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, Span<byte> destination, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 3;
            destination[offset] = a[i];
            destination[offset + 1] = b[i];
            destination[offset + 2] = c[i];
        }
    }

    private static void InterleaveFour(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, ReadOnlySpan<byte> d, Span<byte> destination, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            destination[offset] = a[i];
            destination[offset + 1] = b[i];
            destination[offset + 2] = c[i];
            destination[offset + 3] = d[i];
        }
    }
}
