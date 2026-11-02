using PeachImage.Formats.Shared.Resampling;

namespace PeachImage.Formats.Shared.Scaling;

/// <summary>
/// The generic fallback for a scaled decode on a format with no native reduction: averages each <c>N x N</c> window of a fully decoded image
/// (clipped at the right and bottom edges) into one pixel, giving the same <c>ceil(size / N)</c> result as a native reduced decode.
/// </summary>
internal static class BoxDownscaler
{
    public static Image Reduce(Image source, int denominator)
    {
        (int width, int height) = DecodeScale.ScaledSize(source.Width, source.Height, denominator);
        var format = source.PixelFormat;
        if (format.GetBytesPerSample() != 1 || format.IsFloat())
        {
            return ImageResizer.Resize(source, width, height, ResamplingFilter.Box);
        }

        int channels = format.GetChannelCount();
        var src = source.GetPixelSpan();
        var result = Image.Create(width, height, format);
        var dst = result.GetPixelSpan();
        int sourceStride = source.Width * channels;
        for (int y = 0; y < height; y++)
        {
            int y0 = y * denominator;
            int y1 = Math.Min(y0 + denominator, source.Height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * denominator;
                int x1 = Math.Min(x0 + denominator, source.Width);
                int count = (y1 - y0) * (x1 - x0);
                for (int c = 0; c < channels; c++)
                {
                    int sum = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        for (int xx = x0; xx < x1; xx++)
                        {
                            sum += src[(yy * sourceStride) + (xx * channels) + c];
                        }
                    }

                    dst[(((y * width) + x) * channels) + c] = (byte)((sum + (count / 2)) / count);
                }
            }
        }

        return result;
    }
}
