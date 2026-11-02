using PeachImage.Formats.Shared.Orientation;

namespace PeachImage.Formats.Shared.Scaling;

/// <summary>Resolves <see cref="DecoderOptions.ScaleDenominator"/> and <see cref="DecoderOptions.TargetSize"/> to the 1/N scale a decode runs at.</summary>
internal static class DecodeScale
{
    /// <summary>True when the options ask for anything other than a full-size decode.</summary>
    public static bool IsRequested(DecoderOptions? options) =>
        options is not null && (options.ScaleDenominator != 1 || options.TargetSize is not null);

    /// <summary>The size an image of <paramref name="width"/> x <paramref name="height"/> decodes to at 1/<paramref name="denominator"/>.</summary>
    public static (int Width, int Height) ScaledSize(int width, int height, int denominator) =>
        ((width + denominator - 1) / denominator, (height + denominator - 1) / denominator);

    /// <summary>
    /// The scale denominator (1, 2, 4 or 8) for an image with the given declared size. With a <see cref="DecoderOptions.TargetSize"/>, the
    /// largest N whose scaled size still covers the target; otherwise <see cref="DecoderOptions.ScaleDenominator"/>. When
    /// <see cref="DecoderOptions.ApplyOrientation"/> is set the target is compared against the displayed (oriented) size.
    /// </summary>
    public static int Resolve(DecoderOptions? options, int width, int height, ImageOrientation orientation)
    {
        if (options is null)
        {
            return 1;
        }

        if (options.ResolvedScaleDenominator > 0)
        {
            return options.ResolvedScaleDenominator;
        }

        int denominator = options.ScaleDenominator;
        if (denominator is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(null, denominator, "The scale denominator must be 1, 2, 4 or 8.");
        }

        if (options.TargetSize is not { } target)
        {
            return denominator;
        }

        if (target.Width <= 0 || target.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(null, target, "The target size must be positive.");
        }

        (int w, int h) = options.ApplyOrientation && ImageOrientationApplier.SwapsDimensions(orientation) ? (height, width) : (width, height);
        int best = 1;
        for (int n = 2; n <= 8; n *= 2)
        {
            (int sw, int sh) = ScaledSize(w, h, n);
            if (sw >= target.Width && sh >= target.Height)
            {
                best = n;
            }
        }

        return best;
    }
}
