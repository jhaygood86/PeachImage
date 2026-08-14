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
        int planeStride = component.Coefficients.BlocksWide * 8;
        int planeRows = component.Coefficients.BlocksHigh * 8;
        var raw = Rent(planeStride * planeRows, rented);

        var dequant = DctKernelSelector.Inverse.PrepareDequantTable(component.QuantizationTable.Values);
        for (int by = 0; by < component.Coefficients.BlocksHigh; by++)
        {
            for (int bx = 0; bx < component.Coefficients.BlocksWide; bx++)
            {
                var block = component.Coefficients.Block(bx, by);
                int outOffset = (by * 8 * planeStride) + (bx * 8);
                DctKernelSelector.Inverse.Transform(block, dequant, raw.AsSpan(outOffset), planeStride);
            }
        }

        var cropped = Rent(component.ActualWidthInSamples * component.ActualHeightInSamples, rented);
        for (int y = 0; y < component.ActualHeightInSamples; y++)
        {
            raw.AsSpan(y * planeStride, component.ActualWidthInSamples)
                .CopyTo(cropped.AsSpan(y * component.ActualWidthInSamples, component.ActualWidthInSamples));
        }

        int hRatio = hMax / component.Frame.HorizontalSamplingFactor;
        int vRatio = vMax / component.Frame.VerticalSamplingFactor;

        if (hRatio == 1 && vRatio == 1)
        {
            return CropOrPad(cropped, component.ActualWidthInSamples, component.ActualHeightInSamples, width, height, rented);
        }

        int upsampledWidth = component.ActualWidthInSamples * hRatio;
        int upsampledHeight = component.ActualHeightInSamples * vRatio;
        var upsampled = Rent(upsampledWidth * upsampledHeight, rented);
        upsampler.Upsample(cropped, component.ActualWidthInSamples, component.ActualHeightInSamples, upsampled, upsampledWidth, upsampledHeight, hRatio, vRatio);

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
