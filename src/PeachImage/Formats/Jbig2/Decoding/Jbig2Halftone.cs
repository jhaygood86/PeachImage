using PeachImage.Formats.Shared.Arithmetic;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>Pattern dictionary (T.88 6.7) and halftone region (6.6) decoding.</summary>
internal static class Jbig2Halftone
{
    private const int MaxPatterns = 1 << 20;

    public static Jbig2Bitmap[] DecodePatternDictionary(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
        {
            throw new Jbig2DecodingException("JBIG2 pattern dictionary is truncated.");
        }

        int flags = data[0];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        int patternWidth = data[1];
        int patternHeight = data[2];
        uint grayMax = ((uint)data[3] << 24) | ((uint)data[4] << 16) | ((uint)data[5] << 8) | data[6];
        if (patternWidth == 0 || patternHeight == 0 || grayMax >= MaxPatterns)
        {
            throw new Jbig2DecodingException("JBIG2 pattern dictionary has invalid dimensions.");
        }

        int count = (int)grayMax + 1;
        int collectiveWidth = count * patternWidth;
        var payload = data[7..];
        Jbig2Bitmap collective;
        if (mmr)
        {
            collective = Jbig2GenericRegion.DecodeMmr(payload, collectiveWidth, patternHeight, out _);
        }
        else
        {
            sbyte[] adaptive = Jbig2GenericRegion.NominalAdaptivePixels(template);
            adaptive[0] = (sbyte)-patternWidth;
            adaptive[1] = 0;
            byte[] contexts = Jbig2ArithmeticContexts.Rent(Jbig2GenericRegion.ContextSize(template));
            try
            {
                var decoder = new MqDecoder(payload);
                collective = Jbig2GenericRegion.DecodeArithmetic(ref decoder, contexts.AsSpan(0, Jbig2GenericRegion.ContextSize(template)), template, false, adaptive, collectiveWidth, patternHeight, null);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(contexts);
            }
        }

        var patterns = new Jbig2Bitmap[count];
        for (int i = 0; i < count; i++)
        {
            patterns[i] = collective.Extract(i * patternWidth, 0, patternWidth, patternHeight);
        }

        return patterns;
    }

    /// <summary>Decodes a halftone region segment's data (after the region information field) into a bitmap of the given size.</summary>
    public static Jbig2Bitmap DecodeHalftoneRegion(ReadOnlySpan<byte> data, int width, int height, IReadOnlyList<Jbig2Bitmap> patterns)
    {
        if (data.Length < 21)
        {
            throw new Jbig2DecodingException("JBIG2 halftone region is truncated.");
        }

        if (patterns.Count == 0)
        {
            throw new Jbig2DecodingException("JBIG2 halftone region has no pattern dictionary.");
        }

        int flags = data[0];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool enableSkip = (flags & 8) != 0;
        int combinationOperator = (flags >> 4) & 7;
        byte defaultPixel = (byte)((flags >> 7) & 1);
        uint gridWidth = ReadUInt32(data[1..]);
        uint gridHeight = ReadUInt32(data[5..]);
        int gridX = (int)ReadUInt32(data[9..]);
        int gridY = (int)ReadUInt32(data[13..]);
        int stepX = (data[17] << 8) | data[18];
        int stepY = (data[19] << 8) | data[20];
        var payload = data[21..];

        if ((long)gridWidth * gridHeight > Internal.Jbig2DecodingLimits.MaxPixelCount)
        {
            throw new Jbig2DecodingException("JBIG2 halftone grid is too large.");
        }

        int gw = (int)gridWidth;
        int gh = (int)gridHeight;
        int patternWidth = patterns[0].Width;
        int patternHeight = patterns[0].Height;
        var region = new Jbig2Bitmap(width, height, defaultPixel);

        Jbig2Bitmap? skip = null;
        if (enableSkip)
        {
            skip = new Jbig2Bitmap(gw, gh);
            for (int m = 0; m < gh; m++)
            {
                for (int n = 0; n < gw; n++)
                {
                    long x = ((long)gridX + ((long)m * stepY) + ((long)n * stepX)) >> 8;
                    long y = ((long)gridY + ((long)m * stepX) - ((long)n * stepY)) >> 8;
                    if (x + patternWidth <= 0 || x >= width || y + patternHeight <= 0 || y >= height)
                    {
                        skip.Data[(m * gw) + n] = 1;
                    }
                }
            }
        }

        int bitsPerValue = 0;
        while ((1 << bitsPerValue) < patterns.Count)
        {
            bitsPerValue++;
        }

        // Gray-scale image decoding (T.88 Annex C): bitplanes from most significant, each XORed with the one above (Gray code).
        var values = new int[gw * gh];
        sbyte[] adaptive = Jbig2GenericRegion.NominalAdaptivePixels(template);
        adaptive[0] = (sbyte)(template <= 1 ? 3 : 2);
        adaptive[1] = -1;

        byte[]? contexts = null;
        try
        {
            var decoder = default(MqDecoder);
            if (!mmr)
            {
                int size = Jbig2GenericRegion.ContextSize(template);
                contexts = Jbig2ArithmeticContexts.Rent(size);
                decoder = new MqDecoder(payload);
            }

            int mmrOffset = 0;
            Jbig2Bitmap? previous = null;
            for (int plane = bitsPerValue - 1; plane >= 0; plane--)
            {
                Jbig2Bitmap bits;
                if (mmr)
                {
                    bits = Jbig2GenericRegion.DecodeMmr(payload[mmrOffset..], gw, gh, out int consumed);
                    mmrOffset += consumed;
                }
                else
                {
                    bits = Jbig2GenericRegion.DecodeArithmetic(ref decoder, contexts.AsSpan(0, Jbig2GenericRegion.ContextSize(template)), template, false, adaptive, gw, gh, skip);
                }

                if (previous is not null)
                {
                    var current = bits.Data;
                    var above = previous.Data;
                    for (int i = 0; i < current.Length; i++)
                    {
                        current[i] ^= above[i];
                    }
                }

                var bitData = bits.Data;
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] |= bitData[i] << plane;
                }

                previous = bits;
            }
        }
        finally
        {
            if (contexts is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(contexts);
            }
        }

        for (int m = 0; m < gh; m++)
        {
            for (int n = 0; n < gw; n++)
            {
                long x = ((long)gridX + ((long)m * stepY) + ((long)n * stepX)) >> 8;
                long y = ((long)gridY + ((long)m * stepX) - ((long)n * stepY)) >> 8;
                int index = Math.Min(values[(m * gw) + n], patterns.Count - 1);
                if (x > int.MaxValue / 2 || x < int.MinValue / 2 || y > int.MaxValue / 2 || y < int.MinValue / 2)
                {
                    continue;
                }

                region.Combine(patterns[index], (int)x, (int)y, combinationOperator);
            }
        }

        return region;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data) =>
        ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
}
