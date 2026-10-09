using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Internal;

/// <summary>
/// Chooses the <see cref="PixelFormat"/> a decode produces: 8-bit sources stay 8-bit, 9..16-bit sources use
/// the 16-bit formats, and floating-point or deeper sources use the float formats. Gray with alpha promotes to
/// RGBA, since there is no gray+alpha pixel format.
/// </summary>
internal static class JxlPixelFormatSelector
{
    /// <summary>Whether the image is CMYK: an ICC-described colour space with a black extra channel (cyan, magenta and yellow are the colour channels).</summary>
    public static bool IsCmyk(JxlImageMetadata metadata)
    {
        if (!metadata.ColorEncoding.WantIcc)
        {
            return false;
        }

        foreach (var channel in metadata.ExtraChannels)
        {
            if (channel.Type == JxlExtraChannelType.Black)
            {
                return true;
            }
        }

        return false;
    }

    public static PixelFormat Select(JxlImageMetadata metadata)
    {
        if (IsCmyk(metadata))
        {
            return PixelFormat.Cmyk32;
        }

        bool gray = metadata.ColorEncoding.ColorSpace == JxlColorSpace.Gray;
        bool alpha = metadata.AlphaChannelIndex >= 0;
        var depth = metadata.BitDepth;
        int tier = depth.IsFloat || depth.BitsPerSample > 16 ? 2 : depth.BitsPerSample > 8 ? 1 : 0;

        if (gray && !alpha)
        {
            return tier switch { 0 => PixelFormat.Gray8, 1 => PixelFormat.Gray16, _ => PixelFormat.GrayF32 };
        }

        if (alpha)
        {
            return tier switch { 0 => PixelFormat.Rgba32, 1 => PixelFormat.Rgba64, _ => PixelFormat.RgbaF32 };
        }

        return tier switch { 0 => PixelFormat.Rgb24, 1 => PixelFormat.Rgb48, _ => PixelFormat.RgbF32 };
    }
}
