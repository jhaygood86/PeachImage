using System.Runtime.InteropServices;
using PeachImage.Formats.Png.Internal;

namespace PeachImage.Formats.Png.Decoding;

/// <summary>Combines unpacked samples (plus palette/tRNS state) into final output pixel bytes.</summary>
internal static class PngRowResolver
{
    public static void Resolve(
        ReadOnlySpan<ushort> samples,
        int pixelCount,
        PngHeader header,
        PngPalette? palette,
        ushort[]? trnsKey,
        ReadOnlySpan<ushort> sampleLut,
        bool is16BitOutput,
        Span<byte> destination)
    {
        switch (header.ColorType)
        {
            case PngColorType.Grayscale:
                ResolveGrayscale(samples, pixelCount, trnsKey, sampleLut, is16BitOutput, destination);
                return;

            case PngColorType.Truecolor:
                ResolveTruecolor(samples, pixelCount, trnsKey, sampleLut, is16BitOutput, destination);
                return;

            case PngColorType.Palette:
                ResolvePalette(samples, pixelCount, palette!, destination);
                return;

            case PngColorType.GrayscaleAlpha:
                ResolveGrayscaleAlpha(samples, pixelCount, sampleLut, is16BitOutput, destination);
                return;

            case PngColorType.TruecolorAlpha:
                ResolveTruecolorAlpha(samples, pixelCount, sampleLut, is16BitOutput, destination);
                return;

            default:
                throw new PngDecodingException($"Unsupported PNG color type {(byte)header.ColorType}.");
        }
    }

    private static void ResolveGrayscale(ReadOnlySpan<ushort> samples, int pixelCount, ushort[]? trnsKey, ReadOnlySpan<ushort> lut, bool is16, Span<byte> destination)
    {
        if (trnsKey is null)
        {
            if (is16)
            {
                var dst = MemoryMarshal.Cast<byte, ushort>(destination);
                for (int i = 0; i < pixelCount; i++)
                {
                    dst[i] = lut[samples[i]];
                }
            }
            else
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    destination[i] = (byte)lut[samples[i]];
                }
            }

            return;
        }

        ushort key = trnsKey[0];
        if (is16)
        {
            var dst = MemoryMarshal.Cast<byte, ushort>(destination);
            for (int i = 0; i < pixelCount; i++)
            {
                ushort raw = samples[i];
                ushort value = lut[raw];
                int o = i * 4;
                dst[o] = value;
                dst[o + 1] = value;
                dst[o + 2] = value;
                dst[o + 3] = raw == key ? (ushort)0 : (ushort)65535;
            }
        }
        else
        {
            for (int i = 0; i < pixelCount; i++)
            {
                ushort raw = samples[i];
                byte value = (byte)lut[raw];
                int o = i * 4;
                destination[o] = value;
                destination[o + 1] = value;
                destination[o + 2] = value;
                destination[o + 3] = raw == key ? (byte)0 : (byte)255;
            }
        }
    }

    private static void ResolveTruecolor(ReadOnlySpan<ushort> samples, int pixelCount, ushort[]? trnsKey, ReadOnlySpan<ushort> lut, bool is16, Span<byte> destination)
    {
        if (trnsKey is null)
        {
            if (is16)
            {
                var dst = MemoryMarshal.Cast<byte, ushort>(destination);
                for (int i = 0; i < pixelCount; i++)
                {
                    int s = i * 3;
                    int o = i * 3;
                    dst[o] = lut[samples[s]];
                    dst[o + 1] = lut[samples[s + 1]];
                    dst[o + 2] = lut[samples[s + 2]];
                }
            }
            else
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    int s = i * 3;
                    int o = i * 3;
                    destination[o] = (byte)lut[samples[s]];
                    destination[o + 1] = (byte)lut[samples[s + 1]];
                    destination[o + 2] = (byte)lut[samples[s + 2]];
                }
            }

            return;
        }

        if (is16)
        {
            var dst = MemoryMarshal.Cast<byte, ushort>(destination);
            for (int i = 0; i < pixelCount; i++)
            {
                int s = i * 3;
                int o = i * 4;
                ushort r = samples[s];
                ushort g = samples[s + 1];
                ushort b = samples[s + 2];
                dst[o] = lut[r];
                dst[o + 1] = lut[g];
                dst[o + 2] = lut[b];
                dst[o + 3] = (r == trnsKey[0] && g == trnsKey[1] && b == trnsKey[2]) ? (ushort)0 : (ushort)65535;
            }
        }
        else
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int s = i * 3;
                int o = i * 4;
                ushort r = samples[s];
                ushort g = samples[s + 1];
                ushort b = samples[s + 2];
                destination[o] = (byte)lut[r];
                destination[o + 1] = (byte)lut[g];
                destination[o + 2] = (byte)lut[b];
                destination[o + 3] = (r == trnsKey[0] && g == trnsKey[1] && b == trnsKey[2]) ? (byte)0 : (byte)255;
            }
        }
    }

    private static void ResolvePalette(ReadOnlySpan<ushort> samples, int pixelCount, PngPalette palette, Span<byte> destination)
    {
        bool hasAlpha = palette.HasTransparency;
        int destBpp = hasAlpha ? 4 : 3;
        for (int i = 0; i < pixelCount; i++)
        {
            var (r, g, b, a) = palette.Resolve(samples[i]);
            int o = i * destBpp;
            destination[o] = r;
            destination[o + 1] = g;
            destination[o + 2] = b;
            if (hasAlpha)
            {
                destination[o + 3] = a;
            }
        }
    }

    private static void ResolveGrayscaleAlpha(ReadOnlySpan<ushort> samples, int pixelCount, ReadOnlySpan<ushort> lut, bool is16, Span<byte> destination)
    {
        if (is16)
        {
            var dst = MemoryMarshal.Cast<byte, ushort>(destination);
            for (int i = 0; i < pixelCount; i++)
            {
                int s = i * 2;
                int o = i * 4;
                ushort value = lut[samples[s]];
                dst[o] = value;
                dst[o + 1] = value;
                dst[o + 2] = value;
                dst[o + 3] = samples[s + 1];
            }
        }
        else
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int s = i * 2;
                int o = i * 4;
                byte value = (byte)lut[samples[s]];
                destination[o] = value;
                destination[o + 1] = value;
                destination[o + 2] = value;
                destination[o + 3] = (byte)samples[s + 1];
            }
        }
    }

    private static void ResolveTruecolorAlpha(ReadOnlySpan<ushort> samples, int pixelCount, ReadOnlySpan<ushort> lut, bool is16, Span<byte> destination)
    {
        if (is16)
        {
            var dst = MemoryMarshal.Cast<byte, ushort>(destination);
            for (int i = 0; i < pixelCount; i++)
            {
                int s = i * 4;
                int o = i * 4;
                dst[o] = lut[samples[s]];
                dst[o + 1] = lut[samples[s + 1]];
                dst[o + 2] = lut[samples[s + 2]];
                dst[o + 3] = samples[s + 3];
            }
        }
        else
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int s = i * 4;
                int o = i * 4;
                destination[o] = (byte)lut[samples[s]];
                destination[o + 1] = (byte)lut[samples[s + 1]];
                destination[o + 2] = (byte)lut[samples[s + 2]];
                destination[o + 3] = (byte)samples[s + 3];
            }
        }
    }
}
