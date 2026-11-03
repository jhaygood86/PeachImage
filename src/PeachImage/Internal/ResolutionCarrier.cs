namespace PeachImage;

/// <summary>Carries the density a decoder read through the pixel-format conversion that <see cref="DecoderOptions.TargetPixelFormat"/> asks for, which builds a new image.</summary>
internal static class ResolutionCarrier
{
    public static void Copy(Image from, Image to)
    {
        if (ReferenceEquals(from, to))
        {
            return;
        }

        from.Metadata.ApplyExifResolution();
        to.Metadata.HorizontalResolution ??= from.Metadata.HorizontalResolution;
        to.Metadata.VerticalResolution ??= from.Metadata.VerticalResolution;
    }
}
