namespace PeachImage.Formats.Webp.Kernels;

/// <summary>Scalar fallback tier, used when no SIMD width is hardware-accelerated.</summary>
internal sealed class ScalarWebpPixelPackKernel : IWebpPixelPackKernel
{
    public bool GatherRgba32(ReadOnlySpan<byte> rgba, Span<uint> argb)
    {
        bool allOpaque = true;

        for (int i = 0; i < argb.Length; i++)
        {
            int o = i * 4;
            byte r = rgba[o];
            byte g = rgba[o + 1];
            byte b = rgba[o + 2];
            byte a = rgba[o + 3];
            allOpaque &= a == 0xFF;
            argb[i] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }

        return !allOpaque;
    }

    public void ExtractRgb(ReadOnlySpan<uint> argb, Span<byte> rgb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            uint pixel = argb[i];
            int o = i * 3;
            rgb[o + 0] = (byte)(pixel >> 16);
            rgb[o + 1] = (byte)(pixel >> 8);
            rgb[o + 2] = (byte)pixel;
        }
    }
}
